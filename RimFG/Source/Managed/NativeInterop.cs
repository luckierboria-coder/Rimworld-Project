using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace RimFG
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct FrameMetadata
    {
        public uint abiVersion;
        public uint frameIndex;
        public int screenWidth;
        public int screenHeight;
        public float cameraX;
        public float cameraY;
        public float cameraZ;
        public float orthographicSize;
        public float unscaledDeltaTime;
        public int paused;
        public int gameSpeed;
        public int hudRectCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct HudRect
    {
        public float x;
        public float y;
        public float width;
        public float height;

        public HudRect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }
    }

    internal enum PresentMode
    {
        Disabled = 0,
        ImmediateValidation = 1,
        VSync2x = 2
    }

    internal enum GpuQualityTier
    {
        ResidualFlow = 0,
        CameraOnly = 1,
        Bypass = 2
    }

    internal static class NativeInterop
    {
        internal const uint AbiVersion = 1;
        private const string DllName = "RimFG.Native";
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

        private static IntPtr nativeModule;
        private static string modRoot;
        internal static string LastLoadError { get; private set; }
        internal static string LoadedNativePath { get; private set; }
        internal static bool IsExplicitlyLoaded => nativeModule != IntPtr.Zero;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        internal static void ConfigureModRoot(string root)
        {
            if (!string.IsNullOrEmpty(root))
                modRoot = root;
        }

        internal static bool EnsureNativeLoaded(out string error)
        {
            if (nativeModule != IntPtr.Zero)
            {
                error = null;
                return true;
            }

            string[] candidates;
            if (!string.IsNullOrEmpty(modRoot))
            {
                candidates = new[]
                {
                    Path.Combine(modRoot, "Assemblies", "RimFG.Native.dll"),
                    Path.Combine(modRoot, "Plugins", "x86_64", "RimFG.Native.dll")
                };
            }
            else
            {
                candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RimFG.Native.dll")
                };
            }

            string last = null;
            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                {
                    last = "Native DLL not found at: " + path;
                    continue;
                }

                IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
                int loadError = Marshal.GetLastWin32Error();
                if (module == IntPtr.Zero)
                {
                    // Older Windows configurations can reject the search flags even
                    // though a normal absolute-path load is valid. Retry without them.
                    module = LoadLibraryW(path);
                    loadError = Marshal.GetLastWin32Error();
                }

                if (module == IntPtr.Zero)
                {
                    last = "LoadLibrary failed for '" + path + "' (Win32 " + loadError + ": " + new Win32Exception(loadError).Message + ").";
                    continue;
                }

                // Verify the ABI entrypoint before allowing ordinary DllImport calls.
                IntPtr entry = GetProcAddress(module, "RimFG_GetRenderEventFunc");
                if (entry == IntPtr.Zero)
                {
                    int entryError = Marshal.GetLastWin32Error();
                    last = "RimFG.Native.dll loaded from '" + path + "' but required entrypoint RimFG_GetRenderEventFunc is missing (Win32 " + entryError + ").";
                    continue;
                }

                nativeModule = module;
                LoadedNativePath = path;
                LastLoadError = null;
                error = null;
                return true;
            }

            LastLoadError = last ?? "No RimFG.Native.dll candidate path was available.";
            error = LastLoadError;
            return false;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr RimFG_GetRenderEventFunc();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_SubmitFrameState(ref FrameMetadata metadata, [In] HudRect[] rects, int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_SetSceneTexture(IntPtr nativeTexture, int width, int height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_IsD3D11Ready();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_HasGeneratedFrame();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_StartPresentHook();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_HasUnitySwapChain();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_SetPresentMode(int mode);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_GetPresentMode();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong RimFG_GetGeneratedPresentCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong RimFG_GetSkippedPresentCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_GetGpuQualityTier();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double RimFG_GetGpuFrameGenerationMs();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_StopPresentHook();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_SetEnabled(int enabled);
    }
}
