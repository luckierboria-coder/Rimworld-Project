$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.9 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T27.9 Package Context Consolidation
# - T20/T21 transaction becomes the single JobGiver_Work package lifecycle authority.
# - GlobalNearest/T22/T4/DoBill borrow the T20 package serial instead of maintaining independent scope identities.
# - V0.4.14 PersistentMapSearchFabric consumer is retired after runtime proof of 331 worker batches / 319 snapshot hits / 0 accelerations.
# No new gameplay result cache is added. Vanilla final authority remains unchanged.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.8-job-search-foundation";' 'internal const string Version = "0.9.3-t27.9-package-context";' 'version'
$boot=$boot.Replace('[RimMT] V0.9.3-T27.8 Job Search Foundation initialized.','[RimMT] V0.9.3-T27.9 Package Context Consolidation initialized.')
$boot=Replace-OrThrow $boot '                AdaptiveGenClosestAssist.Apply(harmony);' @'
                // T27.9: retire the persistent-map-fabric consumer. Runtime evidence showed
                // worker publication but zero accelerated calls; keeping ThingGrid hooks and
                // worker batches would be pure overhead. Other GenClosest optimizers remain live.
                FeatureGate.Suppress("parallel.jobPartition", "T27.9 retired PersistentMapSearchFabric after runtime accelerated=0");
'@ 'retire persistent map fabric consumer'
Set-Content $bootPath $boot -Encoding UTF8

# T20 owns the one authoritative package identity.
$t20Path='RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw
$t20=Replace-OrThrow $t20 @'
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static TransactionContext current;
'@ @'
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static TransactionContext current;
        [ThreadStatic] private static long currentPackageSerial;
        private static long packageSerialCounter;

        // T27.9 shared package context API. Consumers may use identity/lifetime only;
        // they do not receive access to another subsystem's memo dictionaries.
        internal static bool InPackage { get { return depth > 0 && current != null; } }
        internal static long CurrentPackageSerial { get { return InPackage ? currentPackageSerial : 0L; } }
        internal static Pawn CurrentPawn { get { return InPackage ? current.Pawn : null; } }
'@ 'T20 shared package API'
$t20=Replace-OrThrow $t20 @'
                TransactionContext context = new TransactionContext(__0);
                current = context;
                __state.Context = context;
                Interlocked.Increment(ref packages);
'@ @'
                TransactionContext context = new TransactionContext(__0);
                current = context;
                currentPackageSerial = Interlocked.Increment(ref packageSerialCounter);
                __state.Context = context;
                Interlocked.Increment(ref packages);
'@ 'T20 assign serial'
$t20=Replace-OrThrow $t20 @'
            if (__state.Outermost)
            {
                if (ReferenceEquals(current, __state.Context)) current = null;
            }
'@ @'
            if (__state.Outermost)
            {
                if (ReferenceEquals(current, __state.Context))
                {
                    current = null;
                    currentPackageSerial = 0L;
                }
            }
'@ 'T20 clear serial'
$t20=$t20.Replace('"T20 foundation transaction: installed=" + installed +','"T20 foundation transaction: installed=" + installed +')
Set-Content $t20Path $t20 -Encoding UTF8

# GlobalNearest keeps its package-local plan type, but no longer patches JobGiver_Work to own a second lifecycle.
$globalPath='RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs'
$global=Get-Content $globalPath -Raw
$global=Replace-OrThrow $global @'
        [ThreadStatic] private static int jobGiverDepth;
        [ThreadStatic] private static long jobGiverStartTicks;
        [ThreadStatic] private static PackageContext current;

        internal static bool InJobGiverScope { get { return jobGiverDepth > 0; } }
        internal static long CurrentScopeStartTicks { get { return jobGiverDepth > 0 ? jobGiverStartTicks : 0L; } }
'@ @'
        // Legacy fields/methods remain source-compatible, but T27.9 no longer installs their
        // JobGiver_Work lifecycle patch. The T20 package serial is the sole scope authority.
        [ThreadStatic] private static int jobGiverDepth;
        [ThreadStatic] private static long jobGiverStartTicks;
        [ThreadStatic] private static long borrowedPackageSerial;
        [ThreadStatic] private static PackageContext current;

        internal static bool InJobGiverScope { get { return JobSearchTransaction093T20.InPackage; } }
        internal static long CurrentScopeStartTicks { get { return JobSearchTransaction093T20.CurrentPackageSerial; } }
'@ 'GlobalNearest borrowed scope fields'
$oldPatch=@'
                MethodBase jobGiver = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (jobGiver == null) return;

                harmony.Patch(jobGiver,
                    prefix: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverFinalizer)) { priority = Priority.Last });

'@
$global=Replace-OrThrow $global $oldPatch @'
                // T27.9: package lifetime/identity is borrowed from JobSearchTransaction093T20.
                // No additional TryIssueJobPackage prefix/finalizer is installed here.

'@ 'remove GlobalNearest package patch'
$global=Replace-OrThrow $global @'
            PackageContext context = current;
            if (context == null) return;
'@ @'
            long packageSerial = JobSearchTransaction093T20.CurrentPackageSerial;
            if (packageSerial <= 0L) return;
            PackageContext context = current;
            if (context == null || borrowedPackageSerial != packageSerial)
            {
                borrowedPackageSerial = packageSerial;
                context = new PackageContext(JobSearchTransaction093T20.CurrentPawn);
                current = context;
            }
'@ 'GlobalNearest lazy package context'
Set-Content $globalPath $global -Encoding UTF8

# T22 generated source exists after the T27.6/T27.7/T27.8 baseline reconstruction.
# It borrows the same T20 serial and therefore stops adding another JobGiver_Work lifecycle patch.
$t22Path='RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
if(-not (Test-Path $t22Path)){ throw 'T27.9 requires generated GenClosestTransactionIndex093T22.cs' }
$t22=Get-Content $t22Path -Raw
$t22=Replace-OrThrow $t22 @'
        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;
'@ @'
        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static long borrowedPackageSerial;
        [ThreadStatic] private static PackageContext current;
'@ 'T22 borrowed serial field'
$t22Patch=@'
                MethodBase package = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (package != null)
                {
                    harmony.Patch(package,
                        prefix: new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), nameof(PackagePrefix))
                        { priority = Priority.First + 200 },
                        finalizer: new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), nameof(PackageFinalizer))
                        { priority = Priority.Last - 200 });
                    packagePatched = true;
                }

'@
$t22=Replace-OrThrow $t22 $t22Patch @'
                // T27.9: borrow the T20 package lifecycle instead of installing a third
                // TryIssueJobPackage prefix/finalizer. packagePatched now means scope available.
                packagePatched = true;

'@ 'remove T22 package patch'
$t22=Replace-OrThrow $t22 @'
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || !RimMTThreadGuard.IsMainThread)
                return true;
'@ @'
            long packageSerial = JobSearchTransaction093T20.CurrentPackageSerial;
            if (packageSerial <= 0L || !RimMTThreadGuard.IsMainThread)
                return true;
            PackageContext context = current;
            if (context == null || borrowedPackageSerial != packageSerial)
            {
                borrowedPackageSerial = packageSerial;
                context = new PackageContext();
                current = context;
                Interlocked.Increment(ref packages);
            }
'@ 'T22 lazy package context'
Set-Content $t22Path $t22 -Encoding UTF8

# Migrate all remaining scope consumers that used GlobalNearest only as a lifecycle clock.
$billPath='RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs'
$bill=Get-Content $billPath -Raw
$bill=$bill.Replace('JobGiverGlobalNearest04181.InJobGiverScope','JobSearchTransaction093T20.InPackage')
$bill=$bill.Replace('JobGiverGlobalNearest04181.CurrentScopeStartTicks','JobSearchTransaction093T20.CurrentPackageSerial')
Set-Content $billPath $bill -Encoding UTF8

$t4Path='RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs'
$t4=Get-Content $t4Path -Raw
$t4=$t4.Replace('JobGiverGlobalNearest04181.InJobGiverScope','JobSearchTransaction093T20.InPackage')
$t4=$t4.Replace('JobGiverGlobalNearest04181.CurrentScopeStartTicks','JobSearchTransaction093T20.CurrentPackageSerial')
Set-Content $t4Path $t4 -Encoding UTF8

$about='RimMT/About/About.xml'
if(Test-Path $about){
    $a=Get-Content $about -Raw
    $a=$a.Replace('V0.9.3-T27.8 Job Search Foundation','V0.9.3-T27.9 Package Context Consolidation')
    Set-Content $about $a -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.9 Package Context Consolidation: T20 package authority shared; persistent map fabric retired.'
