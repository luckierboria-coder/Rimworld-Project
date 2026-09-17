$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T27.6 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T27.6 Production Lean
# - remove legacy resident diagnostic Harmony installs from production runtime
# - restore the production-only DoSingleTick adaptive-load sampler (T0 observatory no longer owns it)
# - retire the zero-yield DoBill worker-tail GenClosest prefix
# - revert ineffective same-package DoBill readiness dictionary reuse
# - expose ReachProfile admission bypass reasons (production counters only)
# - expose HaulMerge authority components instead of one opaque authorityMode=-1

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.5-stutter-foundation";' 'internal const string Version = "0.9.3-t27.6-production-lean";' 'version'

# Resident measurement-only probes now belong to allen.rimmt.diagnostics. Remove their installs only;
# production transaction/parity counters remain in RimMT.dll.
$diagApply=@(
  'TailAttributionPatches093T1.Apply(harmony);',
  'TailPawnPatches093T2.Apply(harmony);',
  'TailPathfinderPatches093T3.Apply(harmony);',
  'PlayerHumanResidualPatches093T14.Apply(harmony);',
  'StorytellerDeepAttribution093T18.Apply(harmony);',
  'QuestDeepAttribution093T19.Apply(harmony);',
  'WorldTailBoundary093T22.Apply(harmony);',
  'WorldRootAttribution093T22.Apply(harmony);',
  'DoBillTailFabric092.Apply(harmony);'
)
foreach($call in $diagApply){
  $boot=[regex]::Replace($boot,'(?m)^\s*'+[regex]::Escape($call)+'\s*\r?\n','')
}
$boot=$boot.Replace(
  '[RimMT] V0.9.3-T27.5 Stutter Foundation initialized. ReachProfile capture is pressure/slow-pair guarded; HaulMerge supports the known CommonSense non-ingestible no-op postfix; DoBill readiness reuses only within one synchronous package; diagnostics remain external; FullParallel hard-OFF.',
  '[RimMT] V0.9.3-T27.6 Production Lean initialized. Legacy resident tail/Pawn/world diagnostic Harmony probes are not installed; dead DoBill worker-tail prefix retired; DoBill package-readiness experiment retired; diagnostics live in allen.rimmt.diagnostics; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

# Restore T0-modified DoSingleTick path to the original production adaptive-load sampler.
$patchPath='RimMT/Source/RimMT/Patches/RimMTPatches.cs'
$patch=Get-Content $patchPath -Raw
$old=@'
        public static void AdaptiveTickPrefix(ref long __state)
        {
            __state = 0L;
            if (Current.ProgramState != ProgramState.Playing)
                return;
            TailObservatory093T0.BeginTick();
            __state = Stopwatch.GetTimestamp();
        }

        public static void AdaptiveTickPostfix(long __state)
        {
            if (__state == 0L) return;
            long end = Stopwatch.GetTimestamp();
            TailObservatory093T0.RecordTick(__state, end);

            // Preserve the stable AdaptiveBurst behavior exactly. T0 measurement is independent.
            if (FeatureGate.IsEnabled("runtime.adaptiveBurst") && !RuntimeCompatibility.ButterPlusPlusActive)
                AdaptiveLoadBalancer.RecordTick(__state);
        }
'@
$new=@'
        public static void AdaptiveTickPrefix(ref long __state)
        {
            __state = 0L;
            if (!FeatureGate.IsEnabled("runtime.adaptiveBurst") || RuntimeCompatibility.ButterPlusPlusActive)
                return;
            __state = Stopwatch.GetTimestamp();
        }

        public static void AdaptiveTickPostfix(long __state)
        {
            if (__state != 0L)
                AdaptiveLoadBalancer.RecordTick(__state);
        }
'@
$patch=Replace-OrThrow $patch $old $new 'restore production DoSingleTick sampler'
Set-Content $patchPath $patch -Encoding UTF8

# Remove T0 correlation callbacks from production hot paths. Existing production counters remain.
$reachPath='RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach=Get-Content $reachPath -Raw
$reach=$reach.Replace('TailObservatory093T0.NoteReachQueryTicks(RecordElapsed(ref queryTicks, ref queryTicksMax, started));','RecordElapsed(ref queryTicks, ref queryTicksMax, started);')
$reach=[regex]::Replace($reach,'(?m)^\s*TailObservatory093T0\.NoteReachCaptureTicks\(elapsed\);\s*\r?\n','')
$reach=[regex]::Replace($reach,'(?m)^\s*TailObservatory093T0\.NoteTopologySliceTicks\(elapsed\);\s*\r?\n','')

# Production-only admission reason counters: the T27.5 report had observed~2M, eligible=0 with every
# later counter also zero. Split the precondition gate so the next report identifies the dead gate.
$reach=Replace-OrThrow $reach @'
        private static long profileCapturePairTicksMax;
'@ @'
        private static long profileCapturePairTicksMax;
        private static long admissionCompatibilityBypass;
        private static long admissionFeatureGateBypass;
        private static long admissionThreadBypass;
        private static long admissionProgramStateBypass;
        private static long admissionCooldownFuseBypass;
        private static long admissionHardFuseBypass;
'@ 'Reach admission counters'
$reach=Replace-OrThrow $reach @'
            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            UpdateRollingFuseMode();
            if (reachFuseMode == ReachFuseMode.Cooldown)
            {
                cooldownLiveBypass++;
                return true;
            }
            if (reachFuseMode == ReachFuseMode.HardFused)
                return true;
'@ @'
            if (!compatibilityReady)
            {
                Interlocked.Increment(ref admissionCompatibilityBypass);
                return true;
            }
            if (!FeatureGate.IsEnabled(FeatureId))
            {
                Interlocked.Increment(ref admissionFeatureGateBypass);
                return true;
            }
            if (!RimMTThreadGuard.IsMainThread)
            {
                Interlocked.Increment(ref admissionThreadBypass);
                return true;
            }
            if (Current.ProgramState != ProgramState.Playing)
            {
                Interlocked.Increment(ref admissionProgramStateBypass);
                return true;
            }

            UpdateRollingFuseMode();
            if (reachFuseMode == ReachFuseMode.Cooldown)
            {
                cooldownLiveBypass++;
                Interlocked.Increment(ref admissionCooldownFuseBypass);
                return true;
            }
            if (reachFuseMode == ReachFuseMode.HardFused)
            {
                Interlocked.Increment(ref admissionHardFuseBypass);
                return true;
            }
'@ 'Reach admission split'
$reach=$reach.Replace('", eligible=" + Interlocked.Read(ref eligible) +', '", admissionBypass[compat/gate/thread/state/cooldown/hard]=" + Interlocked.Read(ref admissionCompatibilityBypass) + "/" + Interlocked.Read(ref admissionFeatureGateBypass) + "/" + Interlocked.Read(ref admissionThreadBypass) + "/" + Interlocked.Read(ref admissionProgramStateBypass) + "/" + Interlocked.Read(ref admissionCooldownFuseBypass) + "/" + Interlocked.Read(ref admissionHardFuseBypass) +`n                ", eligible=" + Interlocked.Read(ref eligible) +')
Set-Content $reachPath $reach -Encoding UTF8

$s4Path='RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4=Get-Content $s4Path -Raw
$s4=[regex]::Replace($s4,'(?m)^\s*TailObservatory093T0\.NoteS4HeavyValidator\(validatorRejects\);\s*\r?\n','')
Set-Content $s4Path $s4 -Encoding UTF8

# The T27.5 package-readiness dictionary reused only 16/7974 builds. Retire it and keep the simple
# live AnyShouldDoNow pass, which is authoritative and allocation-bounded.
$billPath='RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs'
$bill=Get-Content $billPath -Raw
$bill=Replace-OrThrow $bill @'
                sourceIndexHits++;
                PackageReadiness ready = GetPackageReadiness(giver, pawn.Map, things);
                __result = ready == null ? (IEnumerable<Thing>)things : ready.Active;
'@ @'
                sourceIndexHits++;
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
                __result = active == null ? (IEnumerable<Thing>)things : active;
'@ 'retire source package readiness reuse'
$bill=Replace-OrThrow $bill @'
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
'@ @'
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
'@ 'retire ShouldSkip package readiness reuse'
Set-Content $billPath $bill -Encoding UTF8

# HaulMerge: expose each authority component. Do not silently whitelist an unknown target patch.
$mergePath='RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs'
$merge=Get-Content $mergePath -Raw
$merge=Replace-OrThrow $merge @'
        private static long commonSenseIngestibleBypass;
        private static long forcedBypass;
'@ @'
        private static long commonSenseIngestibleBypass;
        private static long authorityTargetForeign;
        private static long authorityThingWithCompsForeign;
        private static long authorityMinifiedForeign;
        private static long authorityThingUnsafe;
        private static long forcedBypass;
'@ 'HaulMerge authority counters'
$merge=Replace-OrThrow $merge @'
                if (HasForeignPatch(target) || HasForeignPatch(thingWithCompsCanStack) || HasForeignPatch(minifiedCanStack))
                {
                    authorityState = -1;
                    return authorityState;
                }

                int thingMode = ThingCanStackAuthorityMode();
                authorityState = thingMode;
                return authorityState;
'@ @'
                bool targetForeign = HasForeignPatch(target);
                bool twcForeign = HasForeignPatch(thingWithCompsCanStack);
                bool minifiedForeign = HasForeignPatch(minifiedCanStack);
                int thingMode = ThingCanStackAuthorityMode();
                if (targetForeign) Interlocked.Increment(ref authorityTargetForeign);
                if (twcForeign) Interlocked.Increment(ref authorityThingWithCompsForeign);
                if (minifiedForeign) Interlocked.Increment(ref authorityMinifiedForeign);
                if (thingMode < 0) Interlocked.Increment(ref authorityThingUnsafe);
                if (targetForeign || twcForeign || minifiedForeign || thingMode < 0)
                {
                    authorityState = -1;
                    return authorityState;
                }
                authorityState = thingMode;
                return authorityState;
'@ 'HaulMerge authority component census'
$merge=Replace-OrThrow $merge @'
                   ", commonSenseIngestibleBypass=" + commonSenseIngestibleBypass +
                   ", forcedBypass=" + forcedBypass +
'@ @'
                   ", commonSenseIngestibleBypass=" + commonSenseIngestibleBypass +
                   ", authorityForeign[target/thingWithComps/minified/thingUnsafe]=" + authorityTargetForeign + "/" + authorityThingWithCompsForeign + "/" + authorityMinifiedForeign + "/" + authorityThingUnsafe +
                   ", forcedBypass=" + forcedBypass +
'@ 'HaulMerge authority summary'
Set-Content $mergePath $merge -Encoding UTF8

# Label/UI.
$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=$about.Replace('V0.9.3-T27.5 Stutter Foundation','V0.9.3-T27.6 Production Lean')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.6 Production Lean.'
