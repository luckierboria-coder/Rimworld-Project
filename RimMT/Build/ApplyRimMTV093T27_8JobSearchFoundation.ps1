$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T27.8 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T27.8 Job Search Foundation
# 1) Diagnostics owner is globally recognized as observation-only by CompatibilityGuard.
# 2) DoBill uses a package-local false-only BillStack readiness memo shared across all DoBill scanners.
# 3) Diagnostics v0.5 does not install GenClosest/Reachability Harmony hooks while search timing is OFF.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.7-dead-path-retirement";' 'internal const string Version = "0.9.3-t27.8-job-search-foundation";' 'version'
$boot=$boot.Replace('[RimMT] V0.9.3-T27.7 Dead Path Retirement initialized.','[RimMT] V0.9.3-T27.8 Job Search Foundation initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# Diagnostics must never suppress production features merely by being present.
$compatPath='RimMT/Source/RimMT/Compatibility/CompatibilityGuard.cs'
$compat=Get-Content $compatPath -Raw
$compat=Replace-OrThrow $compat @'
                if (string.IsNullOrEmpty(owner) || owner == RimMTBootstrap.HarmonyId)
                    continue;

                if (IsAllowedCoexistence(featureId, target, patch, kind))
'@ @'
                if (string.IsNullOrEmpty(owner) || owner == RimMTBootstrap.HarmonyId)
                    continue;

                // allen.rimmt.diagnostics is an observation-only companion owned by this project.
                // Prefix/postfix probes may time a call but must never affect production admission.
                // We intentionally do NOT whitelist a diagnostics transpiler/finalizer here.
                if (string.Equals(owner, "allen.rimmt.diagnostics", StringComparison.Ordinal) &&
                    (string.Equals(kind, "prefix", StringComparison.Ordinal) || string.Equals(kind, "postfix", StringComparison.Ordinal)))
                {
                    MethodInfo dm = patch == null ? null : patch.PatchMethod;
                    string dn = dm == null || dm.DeclaringType == null ? string.Empty : dm.DeclaringType.FullName;
                    if (dn.StartsWith("RimMT.Diagnostics.", StringComparison.Ordinal))
                        continue;
                }

                if (IsAllowedCoexistence(featureId, target, patch, kind))
'@ 'diagnostics coexistence'
Set-Content $compatPath $compat -Encoding UTF8

# Package-local false-only BillStack readiness memo. The map can mutate only on the main thread;
# the memo lifetime is one synchronous JobGiver_Work package and true results are never cached.
$billPath='RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs'
$bill=Get-Content $billPath -Raw
$bill=Replace-OrThrow $bill @'
        private static long shouldSkipContinue;
'@ @'
        private static long shouldSkipContinue;
        [ThreadStatic] private static long readinessScopeStamp;
        [ThreadStatic] private static HashSet<BillStack> inactiveStacksInPackage;
        private static long readinessActualChecks;
        private static long readinessFalseMemoHits;
        private static long readinessFalseMemoStores;
'@ 'readiness memo fields'

$bill=Replace-OrThrow $bill @'
                    BillStack stack = billGiver == null ? null : billGiver.BillStack;

                    bool keep = stack == null || stack.AnyShouldDoNow;
'@ @'
                    BillStack stack = billGiver == null ? null : billGiver.BillStack;

                    bool keep = stack == null || PackageReadinessShouldDoNow(stack);
'@ 'source readiness call'

$bill=Replace-OrThrow $bill @'
                    if (billGiver.BillStack.AnyShouldDoNow)
'@ @'
                    if (PackageReadinessShouldDoNow(billGiver.BillStack))
'@ 'ShouldSkip readiness call'

$helper=@'
        private static bool PackageReadinessShouldDoNow(BillStack stack)
        {
            if (stack == null) return true;

            // Outside a synchronous JobGiver_Work package, never memoize gameplay state.
            if (!JobGiverGlobalNearest04181.InJobGiverScope)
            {
                readinessActualChecks++;
                return stack.AnyShouldDoNow;
            }

            long stamp = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (stamp <= 0L)
            {
                readinessActualChecks++;
                return stack.AnyShouldDoNow;
            }

            if (inactiveStacksInPackage == null)
                inactiveStacksInPackage = new HashSet<BillStack>();
            if (readinessScopeStamp != stamp)
            {
                readinessScopeStamp = stamp;
                inactiveStacksInPackage.Clear();
            }

            if (inactiveStacksInPackage.Contains(stack))
            {
                readinessFalseMemoHits++;
                return false;
            }

            readinessActualChecks++;
            bool active = stack.AnyShouldDoNow;
            if (!active)
            {
                inactiveStacksInPackage.Add(stack);
                readinessFalseMemoStores++;
            }
            return active;
        }

'@
$bill=Replace-OrThrow $bill @'
        private static bool HasUnsafeForeignPatch(MethodBase target)
'@ ($helper + '        private static bool HasUnsafeForeignPatch(MethodBase target)' + "`r`n") 'insert readiness helper'

$bill=Replace-OrThrow $bill @'
                ", shouldSkipContinue=" + shouldSkipContinue + ".";
'@ @'
                ", shouldSkipContinue=" + shouldSkipContinue +
                ", readinessActualChecks=" + readinessActualChecks +
                ", readinessFalseMemoHits=" + readinessFalseMemoHits +
                ", readinessFalseMemoStores=" + readinessFalseMemoStores +
                ", readinessAvoidRate=" + ((readinessActualChecks + readinessFalseMemoHits) <= 0 ? "0.00" : (readinessFalseMemoHits * 100.0 / (readinessActualChecks + readinessFalseMemoHits)).ToString("F2")) + "%" +
                ". Scope=one synchronous JobGiver_Work package; only false AnyShouldDoNow is memoized; true remains live.";
'@ 'readiness memo summary'
Set-Content $billPath $bill -Encoding UTF8

# Diagnostics v0.5: no search hooks are installed when the setting is OFF at startup.
$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.4.0";' 'internal const string Version = "0.5.0";' 'diagnostics version'
$diag=Replace-OrThrow $diag @'
                PatchNamedMethods(harmony, typeof(GenClosest), "ClosestThingReachable", nameof(DiagnosticsPatches.GenClosestPrefix), nameof(DiagnosticsPatches.GenClosestPostfix));
                PatchNamedMethods(harmony, typeof(GenClosest), "ClosestThing_Global", nameof(DiagnosticsPatches.GenClosestPrefix), nameof(DiagnosticsPatches.GenClosestPostfix));
                PatchNamedMethods(harmony, typeof(Reachability), "CanReach", nameof(DiagnosticsPatches.ReachPrefix), nameof(DiagnosticsPatches.ReachPostfix));
'@ @'
                if (RimMTDiagnosticsSettings.EnableSearchTiming)
                {
                    PatchNamedMethods(harmony, typeof(GenClosest), "ClosestThingReachable", nameof(DiagnosticsPatches.GenClosestPrefix), nameof(DiagnosticsPatches.GenClosestPostfix));
                    PatchNamedMethods(harmony, typeof(GenClosest), "ClosestThing_Global", nameof(DiagnosticsPatches.GenClosestPrefix), nameof(DiagnosticsPatches.GenClosestPostfix));
                    PatchNamedMethods(harmony, typeof(Reachability), "CanReach", nameof(DiagnosticsPatches.ReachPrefix), nameof(DiagnosticsPatches.ReachPostfix));
                }
'@ 'conditional search hooks'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.4','RimMT Diagnostics v0.5')
  Set-Content $diagAbout $a -Encoding UTF8
}

$about='RimMT/About/About.xml'
if(Test-Path $about){
  $a=Get-Content $about -Raw
  $a=$a.Replace('V0.9.3-T27.7 Dead Path Retirement','V0.9.3-T27.8 Job Search Foundation')
  Set-Content $about $a -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.8 Job Search Foundation + Diagnostics v0.5 neutrality.'
