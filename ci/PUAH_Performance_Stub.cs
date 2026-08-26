using System;

namespace Verse
{
    public sealed class StaticConstructorOnStartup : Attribute { }

    public static class Log
    {
        public static void Message(string text) { }
        public static void Warning(string text) { }
    }

    public struct IntVec3
    {
        public int x;
        public int y;
        public int z;
    }

    public class Thing
    {
        public IntVec3 Position { get { return default(IntVec3); } }
    }
}
