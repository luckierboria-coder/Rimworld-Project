using System;
using System.Collections.Generic;

namespace Verse
{
    public sealed class StaticConstructorOnStartup : Attribute { }

    public static class Log
    {
        public static void Message(string text) { }
        public static void Warning(string text) { }
        public static void WarningOnce(string text, int key) { }
        public static void Error(string text) { }
    }

    public class Def
    {
        public string defName;
    }

    public class Entity
    {
        public int thingIDNumber;
    }

    public struct IntVec3
    {
        public int x;
        public int y;
        public int z;
    }

    public class Thing : Entity
    {
        public bool Destroyed;
        public bool Spawned;
        public int stackCount;
        public IntVec3 Position { get; set; }
    }

    public class Pawn : Thing
    {
        public string LabelShort { get; set; }
    }

    public struct LocalTargetInfo
    {
        public bool IsValid { get; set; }
        public bool HasThing { get; set; }
        public Thing Thing { get; set; }
    }
}

namespace Verse.AI
{
    using Verse;

    public class JobDef : Def { }

    public class Job
    {
        public List<LocalTargetInfo> targetQueueA;
        public List<int> countQueue;
        public JobDef def;
    }

    public class JobDriver
    {
        public Job job;
        public Pawn pawn;
    }
}

namespace RimWorld
{
    using Verse;
    using Verse.AI;

    public static class HaulAIUtility
    {
        public static Job HaulToStorageJob(Pawn pawn, Thing thing, bool forced)
        {
            return null;
        }
    }
}
