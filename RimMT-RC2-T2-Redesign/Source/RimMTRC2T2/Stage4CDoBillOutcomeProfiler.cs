using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTRC2T2
{
    [StaticConstructorOnStartup]
    internal static class Stage4CDoBillOutcomeProfiler
    {
        private static readonly Harmony H = new Harmony("allen.rimmt");
        private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly FieldInfo DefField = AccessTools.Field(typeof(WorkGiver), "def");
        [ThreadStatic] private static long token;
        [ThreadStatic] private static Dictionary<int, byte> seen;
        private static int patched;
        private static long failures, targetResolveFailures, packages, lastPackage, calls, nulls, jobs, repeats, repeatNulls, repeatJobs;
        private static long b0,b1,b2,b34,b5, e3263,e64127,e128;
        private static double totalMs,nullMs,jobMs,repeatMs,maxMs;
        private static readonly object Gate = new object();
        private static readonly Dictionary<ThingDef,Stat> ThingStats = new Dictionary<ThingDef,Stat>();
        private static readonly Dictionary<WorkGiverDef,Stat> WorkStats = new Dictionary<WorkGiverDef,Stat>();
        private sealed class Stat { public long Calls,Nulls,Jobs,Repeats; public double Ms; }
        public struct State { public bool Sample,Repeated; public long Start; public Thing Thing; public WorkGiverDef WorkDef; public int Bills; public double EntryMs; }

        static Stage4CDoBillOutcomeProfiler() { LongEventHandler.ExecuteWhenFinished(Install); }
        private static void Install()
        {
            try
            {
                foreach (MethodInfo m in typeof(WorkGiver_DoBill).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                {
                    if (m.Name!="JobOnThing" || !typeof(Job).IsAssignableFrom(m.ReturnType)) continue;
                    H.Patch(m, prefix:new HarmonyMethod(typeof(Stage4CDoBillOutcomeProfiler),nameof(Prefix)){priority=Priority.First}, postfix:new HarmonyMethod(typeof(Stage4CDoBillOutcomeProfiler),nameof(Postfix)){priority=Priority.Last});
                    patched++;
                }
                Type d=AccessTools.TypeByName("RimMT.RimMTDiagnostics"); MethodInfo r=d==null?null:AccessTools.Method(d,"LogRuntimeReport");
                if(r!=null) H.Patch(r, postfix:new HarmonyMethod(typeof(Stage4CDoBillOutcomeProfiler),nameof(ReportPostfix)){priority=Priority.Last});
                Log.Message("[RimMT] RC2-T2 Stage 4C.1 DoBill Outcome/Repetition Profiler installed. Target resolver excludes Pawn and prefers IBillGiver; JobOnThing always executes normally and no result is cached or modified.");
            }
            catch(Exception ex){Interlocked.Increment(ref failures); Log.Warning("[RimMT] RC2-T2 Stage 4C.1 failed closed: "+ex.GetType().Name+": "+ex.Message);}
        }

        private static Thing ResolveBillTarget(object[] args)
        {
            if(args==null) return null;
            Thing firstNonPawn=null;
            for(int i=0;i<args.Length;i++)
            {
                Thing t=args[i] as Thing;
                if(t==null || t is Pawn) continue;
                if(t is IBillGiver) return t;
                if(firstNonPawn==null) firstNonPawn=t;
            }
            return firstNonPawn;
        }

        public static void Prefix(object __instance, object[] __args, out State __state)
        {
            __state=default(State);
            try
            {
                if(!PreTailStructureProfiler.IsTailActive) return;
                long t=PreTailStructureProfiler.CurrentJobToken; if(t==0) return;
                Thing thing=ResolveBillTarget(__args);
                if(thing==null){Interlocked.Increment(ref targetResolveFailures);return;}
                if(seen==null) seen=new Dictionary<int,byte>(32); if(token!=t){token=t;seen.Clear();}
                byte c; bool rep=seen.TryGetValue(thing.thingIDNumber,out c); seen[thing.thingIDNumber]=(byte)(c<255?c+1:c);
                if(Interlocked.Read(ref lastPackage)!=t){Interlocked.Exchange(ref lastPackage,t);Interlocked.Increment(ref packages);}
                int bc=-1; IBillGiver bg=thing as IBillGiver; if(bg!=null&&bg.BillStack!=null&&bg.BillStack.Bills!=null) bc=bg.BillStack.Bills.Count;
                WorkGiverDef wd=DefField==null?null:DefField.GetValue(__instance) as WorkGiverDef;
                __state.Sample=true; __state.Repeated=rep; __state.Start=Stopwatch.GetTimestamp(); __state.Thing=thing; __state.WorkDef=wd; __state.Bills=bc; __state.EntryMs=PreTailStructureProfiler.CurrentJobElapsedMs;
            }
            catch{Interlocked.Increment(ref failures);}
        }

        public static void Postfix(Job __result, State __state)
        {
            if(!__state.Sample) return;
            try
            {
                double ms=(Stopwatch.GetTimestamp()-__state.Start)*ToMs; bool n=__result==null;
                Interlocked.Increment(ref calls); if(n)Interlocked.Increment(ref nulls);else Interlocked.Increment(ref jobs);
                if(__state.Repeated){Interlocked.Increment(ref repeats);if(n)Interlocked.Increment(ref repeatNulls);else Interlocked.Increment(ref repeatJobs);}
                int bc=__state.Bills; if(bc==0)Interlocked.Increment(ref b0);else if(bc==1)Interlocked.Increment(ref b1);else if(bc==2)Interlocked.Increment(ref b2);else if(bc>=3&&bc<=4)Interlocked.Increment(ref b34);else if(bc>=5)Interlocked.Increment(ref b5);
                if(__state.EntryMs<64)Interlocked.Increment(ref e3263);else if(__state.EntryMs<128)Interlocked.Increment(ref e64127);else Interlocked.Increment(ref e128);
                lock(Gate){totalMs+=ms;if(n)nullMs+=ms;else jobMs+=ms;if(__state.Repeated)repeatMs+=ms;if(ms>maxMs)maxMs=ms;Add(ThingStats,__state.Thing==null?null:__state.Thing.def,ms,n,__state.Repeated);Add(WorkStats,__state.WorkDef,ms,n,__state.Repeated);}
            }
            catch{Interlocked.Increment(ref failures);}
        }

        private static void Add<T>(Dictionary<T,Stat> map,T key,double ms,bool n,bool rep) where T:class
        { if(key==null)return; Stat s; if(!map.TryGetValue(key,out s)){s=new Stat();map[key]=s;} s.Calls++;s.Ms+=ms;if(n)s.Nulls++;else s.Jobs++;if(rep)s.Repeats++; }

        public static void ReportPostfix()
        {
            long c=Interlocked.Read(ref calls),rp=Interlocked.Read(ref repeats); double t,n,j,r,m; List<KeyValuePair<ThingDef,Stat>> ts; List<KeyValuePair<WorkGiverDef,Stat>> ws;
            lock(Gate){t=totalMs;n=nullMs;j=jobMs;r=repeatMs;m=maxMs;ts=new List<KeyValuePair<ThingDef,Stat>>(ThingStats);ws=new List<KeyValuePair<WorkGiverDef,Stat>>(WorkStats);} ts.Sort((a,b)=>b.Value.Ms.CompareTo(a.Value.Ms)); ws.Sort((a,b)=>b.Value.Ms.CompareTo(a.Value.Ms));
            Log.Message("[RimMT] RC2-T2 Stage 4C.1 DoBill Outcome report: patched="+patched+", packages="+Interlocked.Read(ref packages)+", calls="+c+", result(null/job)="+Interlocked.Read(ref nulls)+"/"+Interlocked.Read(ref jobs)+", repeatedCalls="+rp+" ("+(c==0?0.0:100.0*rp/c).ToString("F1")+"%), repeatedResult(null/job)="+Interlocked.Read(ref repeatNulls)+"/"+Interlocked.Read(ref repeatJobs)+", ms(total/null/job/repeat/max)="+t.ToString("F2")+"/"+n.ToString("F2")+"/"+j.ToString("F2")+"/"+r.ToString("F2")+"/"+m.ToString("F2")+", billCount(0/1/2/3-4/5+)="+Interlocked.Read(ref b0)+"/"+Interlocked.Read(ref b1)+"/"+Interlocked.Read(ref b2)+"/"+Interlocked.Read(ref b34)+"/"+Interlocked.Read(ref b5)+", entryBucket32-63/64-127/128+="+Interlocked.Read(ref e3263)+"/"+Interlocked.Read(ref e64127)+"/"+Interlocked.Read(ref e128)+", targetResolveFailures="+Interlocked.Read(ref targetResolveFailures)+", failures="+Interlocked.Read(ref failures)+".");
            for(int i=0;i<Math.Min(6,ts.Count);i++){var kv=ts[i];var s=kv.Value;Log.Message("[RimMT]   Stage4C.1 ThingDef #"+(i+1)+" "+kv.Key.defName+": calls="+s.Calls+", null/job="+s.Nulls+"/"+s.Jobs+", repeats="+s.Repeats+", totalMs="+s.Ms.ToString("F2")+".");}
            for(int i=0;i<Math.Min(6,ws.Count);i++){var kv=ws[i];var s=kv.Value;Log.Message("[RimMT]   Stage4C.1 WorkGiver #"+(i+1)+" "+kv.Key.defName+": calls="+s.Calls+", null/job="+s.Nulls+"/"+s.Jobs+", repeats="+s.Repeats+", totalMs="+s.Ms.ToString("F2")+".");}
        }
    }
}
