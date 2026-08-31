using System;
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

    internal static class NativeInterop
    {
        internal const uint AbiVersion = 1;
        private const string DllName = "RimFG.Native";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr RimFG_GetRenderEventFunc();

        // Metadata and HUD rectangles are copied into one native double-buffer slot,
        // then atomically published. One managed/native transition per real frame.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_SubmitFrameState(
            ref FrameMetadata metadata,
            [In] HudRect[] rects,
            int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RimFG_IsD3D11Ready();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RimFG_SetEnabled(int enabled);
    }
}
