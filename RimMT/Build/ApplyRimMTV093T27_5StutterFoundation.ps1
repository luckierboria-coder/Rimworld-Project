$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T27.5 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T27.5 production-only changes. New diagnostics stay in allen.rimmt.diagnostics.
# 1) ReachProfile capture circuit: Critical pressure never starts/drains extra Region.Allows work;
#    one >=5ms Region.Allows pair quarantines capture for that map for 3600 frames.
# 2) HaulMerge: permit the known CommonSense Thing.CanStackWith postfix only for NON-ingestible
#    stacks, where CommonSense's own source proves the postfix is a semantic no-op.
# 3) DoBill: reuse one live AnyShouldDoNow scan inside the same synchronous JobGiver package only.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.4-diagnostics-split";' 'internal const string Version = "0.9.3-t27.5-stutter-foundation";' 'version'
$boot=$boot.Replace(
  '[RimMT] V0.9.3-T27.4 Diagnostics Split initialized. T27/T27.1 and T18/T19 source-reordering paths remain retired; T26.1 zero-wait retained; new diagnostics live only in optional allen.rimmt.diagnostics; FullParallel hard-OFF.',
  '[RimMT] V0.9.3-T27.5 Stutter Foundation initialized. ReachProfile capture is pressure/slow-pair guarded; HaulMerge supports the known CommonSense non-ingestible no-op postfix; DoBill readiness reuses only within one synchronous package; diagnostics remain external; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

# ---- ReachProfile capture hardening ----
$reachPath='RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach=Get-Content $reachPath -Raw
$reach=Replace-OrThrow $reach @'
        private const int CaptureWatchdogMicroseconds = 5000;
'@ @'
        private const int CaptureWatchdogMicroseconds = 5000;
        private const long CaptureMapQuarantineFrames = 3600;
'@ 'capture quarantine constant'
$reach=Replace-OrThrow $reach @'
        private static long profileCaptureWatchdogTrips;
'@ @'
        private static long profileCaptureWatchdogTrips;
        private static long profileCapturePressureBypass;
        private static long profileCaptureMapQuarantines;
        private static long profileCapturePairs;
        private static long profileCapturePairTicksMax;
'@ 'capture counters'
$reach=Replace-OrThrow $reach @'
            long now = RimMTRuntime.MainThreadFrames;
            long last = Interlocked.Read(ref slot.LastScheduleFrame);
'@ @'
            long now = RimMTRuntime.MainThreadFrames;
            if (AdaptiveLoadBalancer.Pressure == LoadPressure.Critical)
            {
                Interlocked.Increment(ref profileCapturePressureBypass);
                return;
            }
            long captureDisabledUntil = Interlocked.Read(ref mapState.CaptureDisabledUntilFrame);
            if (captureDisabledUntil > now)
            {
                Interlocked.Increment(ref profileCapturePressureBypass);
                return;
            }
            long last = Interlocked.Read(ref slot.LastScheduleFrame);
'@ 'capture admission guard'
$reach=Replace-OrThrow $reach @'
        private static void DrainProfileCaptureBudget()
        {
            long frame = RimMTRuntime.MainThreadFrames;
'@ @'
        private static void DrainProfileCaptureBudget()
        {
            long frame = RimMTRuntime.MainThreadFrames;
            if (AdaptiveLoadBalancer.Pressure == LoadPressure.Critical)
            {
                while (PendingProfileCaptures.Count != 0)
                {
                    Interlocked.Increment(ref profileCapturePressureBypass);
                    DropCurrentCapture(false, false);
                }
                return;
            }
'@ 'capture drain pressure guard'
$reach=Replace-OrThrow $reach @'
                    long pairTicks = Stopwatch.GetTimestamp() - pairStart;
                    capture.Cursor++;

                    // We cannot pre-empt one foreign/Vanilla Region.Allows call, but once a single
                    // region exceeds the watchdog we stop the capture and quarantine the slot briefly
                    // rather than allowing the remainder of the map to amplify the stall.
                    if (pairTicks >= watchdogTicks)
                    {
                        Interlocked.Exchange(ref capture.Slot.DisabledUntilFrame, frame + 120);
                        RecordProfileCaptureSlice(sliceStart);
                        DropCurrentCapture(false, true);
                        dropped = true;
                        break;
                    }
'@ @'
                    long pairTicks = Stopwatch.GetTimestamp() - pairStart;
                    capture.Cursor++;
                    Interlocked.Increment(ref profileCapturePairs);
                    UpdateMax(ref profileCapturePairTicksMax, pairTicks);

                    // One Region.Allows pair cannot be pre-empted. If it alone exceeds the watchdog,
                    // stop creating extra profile work for this map for ~60 seconds. Vanilla/T21
                    // remain authoritative; this prevents one pathological region/mod interaction
                    // from being multiplied across many Pawn profile captures.
                    if (pairTicks >= watchdogTicks)
                    {
                        Interlocked.Exchange(ref capture.Slot.DisabledUntilFrame, frame + CaptureMapQuarantineFrames);
                        Interlocked.Exchange(ref capture.MapState.CaptureDisabledUntilFrame, frame + CaptureMapQuarantineFrames);
                        Interlocked.Increment(ref profileCaptureMapQuarantines);
                        RecordProfileCaptureSlice(sliceStart);
                        DropCurrentCapture(false, true);
                        dropped = true;
                        break;
                    }
'@ 'slow pair map quarantine'
$reach=Replace-OrThrow $reach @'
            internal TopologySnapshot Topology;
            internal TopologyBuildState TopologyBuild;
'@ @'
            internal TopologySnapshot Topology;
            internal TopologyBuildState TopologyBuild;
            internal long CaptureDisabledUntilFrame;
'@ 'map capture quarantine state'
$reach=Replace-OrThrow $reach @'
                ", captureWatchdogTrips=" + Interlocked.Read(ref profileCaptureWatchdogTrips) +
                ", capturePending=" + PendingProfileCaptures.Count +
'@ @'
                ", captureWatchdogTrips=" + Interlocked.Read(ref profileCaptureWatchdogTrips) +
                ", capturePressureBypass=" + Interlocked.Read(ref profileCapturePressureBypass) +
                ", captureMapQuarantines=" + Interlocked.Read(ref profileCaptureMapQuarantines) +
                ", capturePairs=" + Interlocked.Read(ref profileCapturePairs) +
                ", maxCapturePairUs=" + (Interlocked.Read(ref profileCapturePairTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", capturePending=" + PendingProfileCaptures.Count +
'@ 'capture summary'
Set-Content $reachPath $reach -Encoding UTF8

# ---- HaulMerge / known CommonSense compatibility ----
$mergePath='RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs'
$merge=Get-Content $mergePath -Raw
$merge=Replace-OrThrow $merge @'
        private static long foreignPatchBypass;
        private static long forcedBypass;
'@ @'
        private static long foreignPatchBypass;
        private static long commonSenseCompatibleCalls;
        private static long commonSenseIngestibleBypass;
        private static long forcedBypass;
'@ 'merge counters'
$merge=Replace-OrThrow $merge @'
            if (!AuthoritySafe())
            {
                foreignPatchBypass++;
                return true;
            }

            if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
'@ @'
            int authority = AuthorityMode();
            if (authority < 0)
            {
                foreignPatchBypass++;
                return true;
            }
            if (authority == 2)
            {
                // CommonSense MealStacking postfix returns immediately when other.def.IsIngestible
                // is false. Keep ingestible stacks fully Vanilla/CommonSense authoritative; restore
                // the index only for the proven semantic no-op domain.
                if (t != null && t.def != null && t.def.IsIngestible)
                {
                    commonSenseIngestibleBypass++;
                    return true;
                }
                commonSenseCompatibleCalls++;
            }

            if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
'@ 'merge authority mode use'
$merge=$merge.Replace('        private static bool AuthoritySafe()','        private static int AuthorityMode()')
$merge=Replace-OrThrow $merge @'
            if (authorityState != 0 && (c & AuthorityRecheckMask) != 1)
                return authorityState > 0;

            try
            {
                if (HasForeignPatch(target) || HasForeignPatch(thingCanStack) ||
                    HasForeignPatch(thingWithCompsCanStack) || HasForeignPatch(minifiedCanStack))
                {
                    authorityState = -1;
                    return false;
                }
                authorityState = 1;
                return true;
            }
            catch
            {
                authorityState = -1;
                return false;
            }
'@ @'
            if (authorityState != 0 && (c & AuthorityRecheckMask) != 1)
                return authorityState;

            try
            {
                if (HasForeignPatch(target) || HasForeignPatch(thingWithCompsCanStack) || HasForeignPatch(minifiedCanStack))
                {
                    authorityState = -1;
                    return authorityState;
                }

                int thingMode = ThingCanStackAuthorityMode();
                authorityState = thingMode;
                return authorityState;
            }
            catch
            {
                authorityState = -1;
                return authorityState;
            }
'@ 'merge authority implementation'
$merge=Replace-OrThrow $merge @'
        private static bool HasForeignPatch(MethodBase method)
'@ @'
        // 1 = no foreign patch; 2 = only known CommonSense meal postfix; -1 = unsafe/unknown.
        private static int ThingCanStackAuthorityMode()
        {
            if (thingCanStack == null) return -1;
            Patches info = Harmony.GetPatchInfo(thingCanStack);
            if (info == null) return 1;
            if (HasForeign(info.Prefixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers)) return -1;
            if (info.Postfixes == null) return 1;
            bool commonSense = false;
            foreach (Patch patch in info.Postfixes)
            {
                if (patch == null || string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) continue;
                MethodInfo pm = patch.PatchMethod;
                string dt = pm == null || pm.DeclaringType == null ? null : pm.DeclaringType.FullName;
                if (string.Equals(patch.owner, "net.avilmask.rimworld.mod.CommonSense", StringComparison.Ordinal) &&
                    string.Equals(dt, "CommonSense.CompIngredients_CanStackWith_CommonSensePatch", StringComparison.Ordinal) &&
                    string.Equals(pm.Name, "Postfix", StringComparison.Ordinal))
                {
                    commonSense = true;
                    continue;
                }
                return -1;
            }
            return commonSense ? 2 : 1;
        }

        private static bool HasForeignPatch(MethodBase method)
'@ 'known CommonSense authority helper'
$merge=$merge.Replace('                   ", authoritySafe=" + (authorityState > 0) +','                   ", authorityMode=" + authorityState +')
$merge=Replace-OrThrow $merge @'
                   ", foreignPatchBypass=" + foreignPatchBypass +
                   ", forcedBypass=" + forcedBypass +
'@ @'
                   ", foreignPatchBypass=" + foreignPatchBypass +
                   ", commonSenseCompatibleCalls=" + commonSenseCompatibleCalls +
                   ", commonSenseIngestibleBypass=" + commonSenseIngestibleBypass +
                   ", forcedBypass=" + forcedBypass +
'@ 'merge summary counters'
Set-Content $mergePath $merge -Encoding UTF8

# ---- DoBill same-package readiness reuse ----
$billPath='RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs'
$bill=Get-Content $billPath -Raw
$bill=Replace-OrThrow $bill @'
        private static int failureLogs;

        private static long sourceLookups;
'@ @'
        private static int failureLogs;
        [ThreadStatic] private static long packageStamp;
        [ThreadStatic] private static Dictionary<WorkGiverDef, PackageReadiness> packageReadiness;

        private static long sourceLookups;
'@ 'DoBill package fields'
$bill=Replace-OrThrow $bill @'
        private static long shouldSkipContinue;
'@ @'
        private static long shouldSkipContinue;
        private static long packageReadinessBuilds;
        private static long packageReadinessReuses;
'@ 'DoBill package counters'
$oldScan=@'
                sourceIndexHits++;
                int scanned = things.Count;
                int localActive = 0;
                int localInactive = 0;

                // With the observed workload ~95% of represented benches are inactive, so reserve
                // only a small active list up front instead of allocating capacity for the whole
                // membership set. List<T> still grows normally if a rare call has more active benches.
                List<Thing> active = null;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    IBillGiver billGiver = thing as IBillGiver;
                    BillStack stack = billGiver == null ? null : billGiver.BillStack;

                    bool keep = stack == null || stack.AnyShouldDoNow;
                    if (keep)
                    {
                        localActive++;
                        if (active != null) active.Add(thing);
                        continue;
                    }

                    localInactive++;
                    if (active == null)
                    {
                        active = new List<Thing>(Math.Min(things.Count, 32));
                        for (int j = 0; j < i; j++) active.Add(things[j]);
                    }
                }

                readinessScans += scanned;
                activeReturned += localActive;
                inactiveFiltered += localInactive;
                __result = active == null ? (IEnumerable<Thing>)things : active;
'@
$newScan=@'
                sourceIndexHits++;
                PackageReadiness ready = GetPackageReadiness(giver, pawn.Map, things);
                __result = ready == null ? (IEnumerable<Thing>)things : ready.Active;
'@
$bill=Replace-OrThrow $bill $oldScan $newScan 'DoBill source scan reuse'
$oldSkip=@'
                BillMapCache cache = Caches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(__instance, pawn.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    IBillGiver billGiver = thing as IBillGiver;
                    if (billGiver == null || ReferenceEquals(thing, pawn) || billGiver.BillStack == null) continue;
                    if (billGiver.BillStack.AnyShouldDoNow)
                    {
                        __result = false;
                        shouldSkipContinue++;
                        return false;
                    }
                }

                __result = true;
                shouldSkipNoWork++;
                return false;
'@
$newSkip=@'
                BillMapCache cache = Caches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(__instance, pawn.Map);
                PackageReadiness ready = GetPackageReadiness(__instance, pawn.Map, things);
                if (ready != null && ready.HasActive)
                {
                    __result = false;
                    shouldSkipContinue++;
                    return false;
                }
                __result = true;
                shouldSkipNoWork++;
                return false;
'@
$bill=Replace-OrThrow $bill $oldSkip $newSkip 'DoBill shouldskip reuse'
$bill=Replace-OrThrow $bill @'
        private static bool HasUnsafeForeignPatch(MethodBase target)
'@ @'
        private static PackageReadiness GetPackageReadiness(WorkGiver_DoBill giver, Map map, List<Thing> things)
        {
            if (giver == null || giver.def == null || map == null || things == null) return null;
            long stamp = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (stamp <= 0L)
            {
                // Outside a synchronous JobGiver package, do not retain readiness.
                return BuildPackageReadiness(things);
            }
            if (packageReadiness == null) packageReadiness = new Dictionary<WorkGiverDef, PackageReadiness>();
            if (packageStamp != stamp)
            {
                packageStamp = stamp;
                packageReadiness.Clear();
            }
            PackageReadiness ready;
            if (packageReadiness.TryGetValue(giver.def, out ready) && ready != null && ready.SourceCount == things.Count)
            {
                packageReadinessReuses++;
                return ready;
            }
            ready = BuildPackageReadiness(things);
            packageReadiness[giver.def] = ready;
            packageReadinessBuilds++;
            return ready;
        }

        private static PackageReadiness BuildPackageReadiness(List<Thing> things)
        {
            int scanned = things.Count;
            int localActive = 0;
            int localInactive = 0;
            List<Thing> active = null;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                IBillGiver billGiver = thing as IBillGiver;
                BillStack stack = billGiver == null ? null : billGiver.BillStack;
                bool keep = stack == null || stack.AnyShouldDoNow;
                if (keep)
                {
                    localActive++;
                    if (active != null) active.Add(thing);
                    continue;
                }
                localInactive++;
                if (active == null)
                {
                    active = new List<Thing>(Math.Min(things.Count, 32));
                    for (int j = 0; j < i; j++) active.Add(things[j]);
                }
            }
            readinessScans += scanned;
            activeReturned += localActive;
            inactiveFiltered += localInactive;
            return new PackageReadiness(active == null ? (IEnumerable<Thing>)things : active, localActive > 0, things.Count);
        }

        private static bool HasUnsafeForeignPatch(MethodBase target)
'@ 'DoBill package helper'
$bill=Replace-OrThrow $bill @'
                ", shouldSkipContinue=" + shouldSkipContinue + ".";
'@ @'
                ", shouldSkipContinue=" + shouldSkipContinue +
                ", packageReadinessBuilds=" + packageReadinessBuilds +
                ", packageReadinessReuses=" + packageReadinessReuses + ".";
'@ 'DoBill summary reuse'
$bill=Replace-OrThrow $bill @'
        private sealed class BillMapCache
'@ @'
        private sealed class PackageReadiness
        {
            internal readonly IEnumerable<Thing> Active;
            internal readonly bool HasActive;
            internal readonly int SourceCount;
            internal PackageReadiness(IEnumerable<Thing> active, bool hasActive, int sourceCount)
            {
                Active = active;
                HasActive = hasActive;
                SourceCount = sourceCount;
            }
        }

        private sealed class BillMapCache
'@ 'DoBill package readiness class'
Set-Content $billPath $bill -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=$about.Replace('V0.9.3-T27.4 Diagnostics Split','V0.9.3-T27.5 Stutter Foundation')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.5 Stutter Foundation: Reach capture hardening, CommonSense-safe non-ingestible HaulMerge, same-package DoBill readiness reuse.'
