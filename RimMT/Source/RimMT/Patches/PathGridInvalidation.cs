namespace RimMT
{
    internal static class PathGridInvalidation
    {
        public static void Postfix() { ReachabilityNoCache.InvalidateTopology(); }
    }
}
