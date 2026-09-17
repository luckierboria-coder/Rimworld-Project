$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.5 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T27.5 — Hard Budget + CommonSense-safe HaulMerge
# Production-only changes:
# 1) ReachProfile capture checks its budget after every Region.Allows pair. A single pair >=2ms
#    hard-disables future profile capture for the run (existing published profiles may age out);
#    pending captures are dropped and Vanilla remains authoritative on misses.
# 2) DoBill worker-tail fabric is retired after long-run evidence showed 5091 tail-eligible calls,
#    zero registrations and zero accelerations. Persistent DoBill membership/readiness remains.
# 3) T4 HaulMerge accepts exactly the known CommonSense Thing.CanStackWith meal postfix only for
#    NON-INGESTIBLE candidates, where that postfix is provably a no-op. Ingestibles still fail open.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.4-diagnostics-split";' 'internal const string Version = "0.9.3-t27.5-hard-budget";' 'version'
$boot=Replace-OrThrow $boot '                DoBillTailFabric092.Apply(harmony);' @'
                // T27.5: retired. Long-run telemetry showed zero production accelerations while this
                // prefix still sat on the GenClosest hot path. PersistentDoBillIndex remains active.
'@ 'retire DoBill worker-tail install'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T27.4 Diagnostics Split initialized. T27/T27.1 and T18/T19 source-reordering paths remain retired; T26.1 zero-wait retained; new diagnostics live only in optional allen.rimmt.diagnostics; FullParallel hard-OFF.',
    '[RimMT] V0.9.3-T27.5 Hard Budget initialized. T27/T27.1 and T18/T19 source-reordering remain retired; DoBill worker-tail retired after zero-yield evidence; ReachProfile capture hard-budget guard active; T4 permits exact CommonSense meal-postfix coexistence only for non-ingestibles; diagnostics remain external; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

# ---- ReachProfile hard-budget guard ----
$reachPath='RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach=Get-Content $reachPath -Raw
$reach=Replace-OrThrow $reach '        private const int CaptureCheckMask = 3;' '        private const int CaptureCheckMask = 0;' 'capture budget check every pair'
$reach=Replace-OrThrow $reach '        private const int CaptureWatchdogMicroseconds = 5000;' '        private const int CaptureWatchdogMicroseconds = 2000;' 'capture watchdog 2ms'
$reach=[regex]::Replace($reach,'private const int SliceCheckMask = \d+;','private const int SliceCheckMask = 0;',1)
$reach=Replace-OrThrow $reach @'
        private static long profileCaptureWatchdogTrips;
'@ @'
        private static long profileCaptureWatchdogTrips;
        private static long profileCaptureHardTrips;
        private static long profileCapturePairTicksMax;
        private static int profileCaptureHardDisabled;
'@ 'reach hard-budget counters'
$reach=Replace-OrThrow $reach @'
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled(FeatureId))
                return;

            long now = RimMTRuntime.MainThreadFrames;
'@ @'
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled(FeatureId))
                return;
            if (Volatile.Read(ref profileCaptureHardDisabled) != 0)
                return;

            long now = RimMTRuntime.MainThreadFrames;
'@ 'block new captures after hard trip'
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
                    UpdateMax(ref profileCapturePairTicksMax, pairTicks);

                    // T27.5: one Region.Allows pair is not pre-emptible. If even one pair crosses
                    // 2ms, continuing to schedule captures can reproduce a 100-500ms hitch. Retire
                    // the capture producer for the rest of this run; published profiles remain
                    // usable until normal age/generation rules invalidate them, and every miss is
                    // Vanilla-authoritative. This is a performance fuse, not a gameplay authority.
                    if (pairTicks >= watchdogTicks)
                    {
                        Interlocked.Exchange(ref capture.Slot.DisabledUntilFrame, frame + 600);
                        RecordProfileCaptureSlice(sliceStart);
                        DropCurrentCapture(false, true);
                        if (Interlocked.CompareExchange(ref profileCaptureHardDisabled, 1, 0) == 0)
                        {
                            Interlocked.Increment(ref profileCaptureHardTrips);
                            double pairMs = pairTicks * 1000.0 / Stopwatch.Frequency;
                            Log.Warning("[RimMT] T27.5 ReachProfile capture HARD-BUDGET fuse: one Region.Allows pair took " +
                                pairMs.ToString("F3") + " ms (limit 2 ms). Future profile captures are disabled for this run; Vanilla Reachability remains authoritative on misses.");
                        }
                        while (PendingProfileCaptures.Count != 0)
                            DropCurrentCapture(false, false);
                        return;
                    }
'@ 'hard-disable pathological Region.Allows capture'
$reach=Replace-OrThrow $reach @'
                ", captureWatchdogTrips=" + Interlocked.Read(ref profileCaptureWatchdogTrips) +
                ", capturePending=" + PendingProfileCaptures.Count +
'@ @'
                ", captureWatchdogTrips=" + Interlocked.Read(ref profileCaptureWatchdogTrips) +
                ", captureHardDisabled=" + (Volatile.Read(ref profileCaptureHardDisabled) != 0) +
                ", captureHardTrips=" + Interlocked.Read(ref profileCaptureHardTrips) +
                ", maxCapturePairUs=" + (Interlocked.Read(ref profileCapturePairTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", capturePending=" + PendingProfileCaptures.Count +
'@ 'reach summary hard-budget fields'
Set-Content $reachPath $reach -Encoding UTF8

# ---- T4 exact CommonSense coexistence ----
$t4Path='RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs'
$t4=Get-Content $t4Path -Raw
$t4=Replace-OrThrow $t4 @'
        private static long foreignPatchBypass;
        private static long forcedBypass;
'@ @'
        private static long foreignPatchBypass;
        private static long commonSenseCompatCalls;
        private static long commonSenseIngestibleBypass;
        private static long forcedBypass;
'@ 'T4 CommonSense counters'
$t4=Replace-OrThrow $t4 @'
            if (!AuthoritySafe())
            {
                foreignPatchBypass++;
                return true;
            }

            if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
'@ @'
            int authorityMode = AuthorityMode();
            if (authorityMode < 1)
            {
                foreignPatchBypass++;
                return true;
            }

            if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
'@ 'T4 authority mode call'
$t4=Replace-OrThrow $t4 @'
            if (!UsesSupportedVanillaStackSemantics(t))
            {
                unsupportedCandidateBypass++;
                return true;
            }

            try
'@ @'
            if (!UsesSupportedVanillaStackSemantics(t))
            {
                unsupportedCandidateBypass++;
                return true;
            }
            // CommonSense's exact CompIngredients postfix changes only ingestible stacking and is
            // a strict no-op for non-ingestibles. Never index an ingestible under this coexistence
            // mode; let the real postfix run in full.
            if (authorityMode == 2)
            {
                if (t.def.IsIngestible)
                {
                    commonSenseIngestibleBypass++;
                    return true;
                }
                commonSenseCompatCalls++;
            }

            try
'@ 'T4 CommonSense candidate gate'
$oldAuthority=@'
        private static bool AuthoritySafe()
        {
            long c = calls;
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
        }
'@
$newAuthority=@'
        // 1 = no foreign stack semantics; 2 = exact CommonSense meal postfix only; -1 = blocked.
        private static int AuthorityMode()
        {
            long c = calls;
            if (authorityState != 0 && (c & AuthorityRecheckMask) != 1)
                return authorityState;

            try
            {
                if (HasForeignPatch(target) || HasForeignPatch(thingWithCompsCanStack) || HasForeignPatch(minifiedCanStack))
                {
                    authorityState = -1;
                    return authorityState;
                }
                authorityState = ThingCanStackAuthorityMode();
                return authorityState;
            }
            catch
            {
                authorityState = -1;
                return authorityState;
            }
        }

        private static int ThingCanStackAuthorityMode()
        {
            if (thingCanStack == null) return -1;
            Patches info = Harmony.GetPatchInfo(thingCanStack);
            if (info == null) return 1;
            if (HasForeign(info.Prefixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers))
                return -1;

            bool sawCommonSense = false;
            foreach (Patch patch in info.Postfixes)
            {
                if (patch == null) continue;
                if (string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) continue;
                MethodInfo method = patch.PatchMethod;
                string typeName = method == null || method.DeclaringType == null ? null : method.DeclaringType.FullName;
                bool exact = string.Equals(patch.owner, "net.avilmask.rimworld.mod.CommonSense", StringComparison.Ordinal) &&
                    method != null && string.Equals(method.Name, "Postfix", StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(typeName) && typeName.IndexOf("CompIngredients_CanStackWith_CommonSensePatch", StringComparison.Ordinal) >= 0;
                if (!exact) return -1;
                sawCommonSense = true;
            }
            return sawCommonSense ? 2 : 1;
        }
'@
$t4=Replace-OrThrow $t4 $oldAuthority $newAuthority 'replace T4 authority classifier'
$t4=Replace-OrThrow $t4 @'
                   ", authoritySafe=" + (authorityState > 0) +
'@ @'
                   ", authoritySafe=" + (authorityState > 0) +
                   ", authorityMode=" + (authorityState == 2 ? "CommonSenseNonIngestible" : (authorityState == 1 ? "Vanilla" : "Blocked")) +
'@ 'T4 summary authority mode'
$t4=Replace-OrThrow $t4 @'
                   ", foreignPatchBypass=" + foreignPatchBypass +
                   ", forcedBypass=" + forcedBypass +
'@ @'
                   ", foreignPatchBypass=" + foreignPatchBypass +
                   ", commonSenseCompatCalls=" + commonSenseCompatCalls +
                   ", commonSenseIngestibleBypass=" + commonSenseIngestibleBypass +
                   ", forcedBypass=" + forcedBypass +
'@ 'T4 summary CommonSense counters'
Set-Content $t4Path $t4 -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27.4 Diagnostics Split','V0.9.3-T27.5 Hard Budget')
$report=$report.Replace(
    'T27.4 retains the T27.2/T27.3 behavior resets, but removes the T27.3 Wait tracer from RimMT.dll. New profiling belongs to optional allen.rimmt.diagnostics; legacy observability remains only where older production paths still consume it. The WorkGiver safety registry remains audit-only; FullParallel stays hard-OFF;',
    'T27.5 keeps diagnostics external, retires the zero-yield DoBill worker-tail GenClosest consumer, adds a 2ms per-Region.Allows capture watchdog that hard-disables future ReachProfile captures for the run after one pathological pair, and permits T4 HaulMerge coexistence with the exact CommonSense meal-stacking postfix only for non-ingestibles where that postfix is a no-op. FullParallel remains hard-OFF;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=$about.Replace('V0.9.3-T27.4 Diagnostics Split','V0.9.3-T27.5 Hard Budget')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.5 Hard Budget: ReachProfile capture fuse, zero-yield DoBill worker-tail retirement, exact CommonSense non-ingestible T4 coexistence.'