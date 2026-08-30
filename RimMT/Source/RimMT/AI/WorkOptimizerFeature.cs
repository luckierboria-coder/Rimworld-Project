using System.Threading;

namespace RimMT
{
    internal sealed class WorkOptimizerFeature
    {
        private readonly string name;
        internal volatile bool Enabled;
        private string reason = "not installed";
        private long invocations;
        private long parityBypasses;
        private long seen;
        private long kept;
        private long pruned;
        private long gateChecks;
        private long gateHits;
        private long indexBuilds;
        private long indexHits;

        internal WorkOptimizerFeature(string name) { this.name = name; }
        internal void Enable(string why) { Enabled = true; reason = why ?? "active"; }
        internal void Disable(string why) { Enabled = false; reason = why ?? "disabled"; }

        internal bool ShouldParityBypass()
        {
            long n = Interlocked.Increment(ref invocations);
            if ((n & CompositeWorkOptimizer090.ParityMask) == 0)
            {
                Interlocked.Increment(ref parityBypasses);
                return true;
            }
            return false;
        }

        internal void Seen() { Interlocked.Increment(ref seen); }
        internal void Kept() { Interlocked.Increment(ref kept); }
        internal void Pruned() { Interlocked.Increment(ref pruned); }
        internal void GateCheck() { Interlocked.Increment(ref gateChecks); }
        internal void GateHit() { Interlocked.Increment(ref gateHits); }
        internal void IndexBuild() { Interlocked.Increment(ref indexBuilds); }
        internal void IndexHit() { Interlocked.Increment(ref indexHits); }

        internal string Summary()
        {
            long s = Interlocked.Read(ref seen);
            long p = Interlocked.Read(ref pruned);
            long gc = Interlocked.Read(ref gateChecks);
            long gh = Interlocked.Read(ref gateHits);
            double prunePct = s == 0 ? 0.0 : p * 100.0 / s;
            double gatePct = gc == 0 ? 0.0 : gh * 100.0 / gc;
            return name + "={enabled=" + Enabled + ", reason=" + reason +
                ", calls=" + Interlocked.Read(ref invocations) +
                ", parityBypass=" + Interlocked.Read(ref parityBypasses) +
                ", seen/kept/pruned=" + s + "/" + Interlocked.Read(ref kept) + "/" + p +
                ", prunePct=" + prunePct.ToString("F1") + "%" +
                ", gateChecks/hits=" + gc + "/" + gh + "(" + gatePct.ToString("F1") + "%)" +
                ", indexBuilds/hits=" + Interlocked.Read(ref indexBuilds) + "/" + Interlocked.Read(ref indexHits) + "}";
        }
    }
}
