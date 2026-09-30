$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.11 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.11 Slow Tail Attribution
# Telemetry-only release: no admission, budget, coverage, ReachProfile or authority behavior changes.
# - classify slow original GenClosest calls by the exact V0.9.10 resumable decision path
# - split structural invalidation causes (especially root movement vs real ownership changes)
# - add sliced-segment tail buckets so handled resumable work is not mixed with Vanilla fallback tails
# - keep all classification on main-thread/thread-local primitives; no extra per-candidate reflection

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        [ThreadStatic] private static bool blockCurrentPackageForPendingEmergency;
'@ @'
        [ThreadStatic] private static bool blockCurrentPackageForPendingEmergency;
        [ThreadStatic] private static int tailPathClass;

        internal const int TailClassUnknown = 0;
        internal const int TailClassPreAdmission = 1;
        internal const int TailClassSupportedUnsliced = 2;
        internal const int TailClassSliced = 3;
        internal const int TailClassExactClosureBypass = 4;
        internal const int TailClassOtherShapeBypass = 5;
        internal static int CurrentTailPathClass { get { return tailPathClass; } }
'@ 'tail path thread-local classification'

$resume = Replace-OrThrow $resume @'
        private static long stateStructuralInvalidations;
        private static long sourceCountChanged;
'@ @'
        private static long stateStructuralInvalidations;
        private static long structuralNullState;
        private static long structuralPackageChanged;
        private static long structuralPawnChanged;
        private static long structuralMapChanged;
        private static long structuralRootChanged;
        private static long structuralMembersMissing;
        private static long sliceOver2;
        private static long sliceOver5;
        private static long sliceOver10;
        private static long sliceOver20;
        private static long sliceOver50;
        private static long sourceCountChanged;
'@ 'structural and sliced-tail counters'

$resume = Replace-OrThrow $resume @'
        public static bool ClosestPrefix(MethodBase __originalMethod, IntVec3 __0, Map __1, ThingRequest __2,
            PathEndMode __3, TraverseParms __4, float __5, Predicate<Thing> __6,
            IEnumerable<Thing> __7, ref Thing __result)
        {
            if (!patched || suspendedThisPackage || !JobGiverGlobalNearest04181.InJobGiverScope ||
'@ @'
        public static bool ClosestPrefix(MethodBase __originalMethod, IntVec3 __0, Map __1, ThingRequest __2,
            PathEndMode __3, TraverseParms __4, float __5, Predicate<Thing> __6,
            IEnumerable<Thing> __7, ref Thing __result)
        {
            tailPathClass = TailClassUnknown;
            if (!patched || suspendedThisPackage || !JobGiverGlobalNearest04181.InJobGiverScope ||
'@ 'reset per-call tail classification'

$resume = Replace-OrThrow $resume @'
                shapeBypass++;
                baseShapeBypass++;
                return true;
'@ @'
                shapeBypass++;
                baseShapeBypass++;
                tailPathClass = TailClassOtherShapeBypass;
                return true;
'@ 'classify base shape bypass'

$resume = Replace-OrThrow $resume @'
                shapeBypass++;
                exactClosureBypass++;
                return true;
'@ @'
                shapeBypass++;
                exactClosureBypass++;
                tailPathClass = TailClassExactClosureBypass;
                return true;
'@ 'classify exact closure bypass'

$resume = Replace-OrThrow $resume @'
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                return true;
            }
'@ @'
            if (!IsSupportedScanner(scanner))
            {
                shapeBypass++;
                tailPathClass = TailClassOtherShapeBypass;
                return true;
            }
'@ 'classify unsupported scanner shape'

$resume = Replace-OrThrow $resume @'
                pendingOtherPackageBypass++;
                return true;
'@ @'
                pendingOtherPackageBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                return true;
'@ 'classify other package fallback'

$resume = Replace-OrThrow $resume @'
                pendingOtherScannerBypass++;
                return true;
'@ @'
                pendingOtherScannerBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                return true;
'@ 'classify other scanner fallback'

$resume = Replace-OrThrow $resume @'
            if (!hasPendingForScanner && !JobGiverTailTelemetry094.IsRecurringHot(scanner))
                return true;
'@ @'
            if (!hasPendingForScanner && !JobGiverTailTelemetry094.IsRecurringHot(scanner))
            {
                tailPathClass = TailClassPreAdmission;
                return true;
            }
'@ 'classify pre-admission slow candidate'

$resume = Replace-OrThrow $resume @'
                authorityBypass++;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
'@ @'
                authorityBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
'@ 'classify authority fallback'

$resume = Replace-OrThrow $resume @'
                if (__7 != null) customEnumerableBypass++;
                else unstableSourceBypass++;
                shapeBypass++;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
'@ @'
                if (__7 != null) customEnumerableBypass++;
                else unstableSourceBypass++;
                shapeBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
'@ 'classify unstable/custom source fallback'

$resume = Replace-OrThrow $resume @'
            catch
            {
                sourceInvalidations++;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
            }
'@ @'
            catch
            {
                sourceInvalidations++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
            }
'@ 'classify source Count exception fallback'

$resume = Replace-OrThrow $resume @'
                    if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                    return true;
                }
            }
            if (count > MaxSourceCount)
'@ @'
                    tailPathClass = TailClassSupportedUnsliced;
                    if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                    return true;
                }
            }
            if (count > MaxSourceCount)
'@ 'classify small source fallback'

$resume = Replace-OrThrow $resume @'
                capacityBypass++;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
'@ @'
                capacityBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
'@ 'classify capacity fallback'

$resume = Replace-OrThrow $resume @'
                if (state == null)
                {
                    if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                    return true;
                }
            }

            try
'@ @'
                if (state == null)
                {
                    tailPathClass = TailClassSupportedUnsliced;
                    if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                    return true;
                }
            }

            tailPathClass = TailClassSliced;
            try
'@ 'mark admitted sliced path'

# Every sliced segment is bucketed using the same thresholds as whole-search telemetry.
$resume = $resume.Replace('if (sliceTicks > maxSliceTicks) maxSliceTicks = sliceTicks;', 'if (sliceTicks > maxSliceTicks) maxSliceTicks = sliceTicks; RecordSliceTail(sliceTicks);')
$resume = $resume.Replace('if (completedSliceTicks > maxSliceTicks) maxSliceTicks = completedSliceTicks;', 'if (completedSliceTicks > maxSliceTicks) maxSliceTicks = completedSliceTicks; RecordSliceTail(completedSliceTicks);')

$oldValidate = @'
            if (state == null || !SamePackageContext(state) || !ReferenceEquals(state.Pawn, pawn) ||
                !ReferenceEquals(state.Map, map) || state.Root != root || state.Members == null)
            {
                stateStructuralInvalidations++;
                return false;
            }
'@
$newValidate = @'
            if (state == null)
            {
                stateStructuralInvalidations++;
                structuralNullState++;
                return false;
            }
            if (!SamePackageContext(state))
            {
                stateStructuralInvalidations++;
                structuralPackageChanged++;
                return false;
            }
            if (!ReferenceEquals(state.Pawn, pawn))
            {
                stateStructuralInvalidations++;
                structuralPawnChanged++;
                return false;
            }
            if (!ReferenceEquals(state.Map, map))
            {
                stateStructuralInvalidations++;
                structuralMapChanged++;
                return false;
            }
            if (state.Root != root)
            {
                stateStructuralInvalidations++;
                structuralRootChanged++;
                return false;
            }
            if (state.Members == null)
            {
                stateStructuralInvalidations++;
                structuralMembersMissing++;
                return false;
            }
'@
$resume = Replace-OrThrow $resume $oldValidate $newValidate 'split structural invalidation causes'

$resume = Replace-OrThrow $resume @'
        private static long SliceBudgetTicks()
'@ @'
        private static void RecordSliceTail(long ticks)
        {
            if (ticks >= Stopwatch.Frequency * 2L / 1000L) sliceOver2++;
            if (ticks >= Stopwatch.Frequency * 5L / 1000L) sliceOver5++;
            if (ticks >= Stopwatch.Frequency * 10L / 1000L) sliceOver10++;
            if (ticks >= Stopwatch.Frequency * 20L / 1000L) sliceOver20++;
            if (ticks >= Stopwatch.Frequency * 50L / 1000L) sliceOver50++;
        }

        private static long SliceBudgetTicks()
'@ 'sliced tail recorder'

$resume = Replace-OrThrow $resume @'
                   ", stateStructuralInvalidations=" + stateStructuralInvalidations +
                   ", sourceCountChanged=" + sourceCountChanged +
'@ @'
                   ", stateStructuralInvalidations=" + stateStructuralInvalidations +
                   " [null=" + structuralNullState +
                   ", package=" + structuralPackageChanged +
                   ", pawn=" + structuralPawnChanged +
                   ", map=" + structuralMapChanged +
                   ", root=" + structuralRootChanged +
                   ", members=" + structuralMembersMissing + "]" +
                   ", slicedSegments=[>2ms=" + sliceOver2 +
                   ", >5ms=" + sliceOver5 +
                   ", >10ms=" + sliceOver10 +
                   ", >20ms=" + sliceOver20 +
                   ", >50ms=" + sliceOver50 + "]" +
                   ", sourceCountChanged=" + sourceCountChanged +
'@ 'structural and sliced-tail summary'

$resume = $resume.Replace('V0.9.10 recurring-hot JobGiver source-reconciled continuation slicer.', 'V0.9.11 recurring-hot JobGiver slow-tail-attributed continuation slicer.')
$resume = $resume.Replace('[RimMT] V0.9.10 Source-Reconciled Continuation installed on ', '[RimMT] V0.9.11 Slow Tail Attribution installed on ')
$resume = $resume.Replace('[RimMT] V0.9.10 Source-Reconciled Continuation failed closed: ', '[RimMT] V0.9.11 Slow Tail Attribution failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.10 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.11 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.10:', 'Resumable JobGiver V0.9.11:')
Set-Content $resumePath $resume -Encoding UTF8

# Reuse the existing whole-call stopwatch, but snapshot the decision-path class after the
# higher-priority resumable prefix has run. The struct state is allocation-free.
$tailPath = 'RimMT/Source/RimMT/Diagnostics/JobGiverTailTelemetry094.cs'
$tail = Get-Content $tailPath -Raw
$tail = Replace-OrThrow $tail @'
        private static long maxTicks;
        private static readonly Dictionary<string, TailStats> Stats = new Dictionary<string, TailStats>();
'@ @'
        private static long maxTicks;
        private static readonly TailStats PreAdmissionPath = new TailStats();
        private static readonly TailStats SupportedUnslicedPath = new TailStats();
        private static readonly TailStats ExactClosurePath = new TailStats();
        private static readonly TailStats OtherShapePath = new TailStats();
        private static readonly TailStats UnknownPath = new TailStats();
        private static readonly Dictionary<string, TailStats> Stats = new Dictionary<string, TailStats>();
'@ 'tail path buckets'

$tail = Replace-OrThrow $tail @'
        public static void Prefix(ref long __state)
        {
            __state = 0L;
            if (!JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;
            __state = Stopwatch.GetTimestamp();
        }

        public static void Postfix(Predicate<Thing> __6, long __state)
        {
            if (__state == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
'@ @'
        public static void Prefix(ref TailCallState __state)
        {
            __state = default(TailCallState);
            if (!JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;
            __state.Start = Stopwatch.GetTimestamp();
            __state.PathClass = ResumableJobGiver095.CurrentTailPathClass;
        }

        public static void Postfix(Predicate<Thing> __6, TailCallState __state)
        {
            if (__state.Start == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Start;
'@ 'allocation-free path snapshot state'

$tail = Replace-OrThrow $tail @'
            over2++;
            if (elapsed >= T5) over5++;
'@ @'
            over2++;
            RecordPathTail(__state.PathClass, elapsed);
            if (elapsed >= T5) over5++;
'@ 'record slow decision path'

$tail = Replace-OrThrow $tail @'
        internal static bool IsRecurringHot(WorkGiver_Scanner scanner)
'@ @'
        private static void RecordPathTail(int pathClass, long elapsed)
        {
            TailStats stat;
            switch (pathClass)
            {
                case ResumableJobGiver095.TailClassPreAdmission: stat = PreAdmissionPath; break;
                case ResumableJobGiver095.TailClassSupportedUnsliced: stat = SupportedUnslicedPath; break;
                case ResumableJobGiver095.TailClassExactClosureBypass: stat = ExactClosurePath; break;
                case ResumableJobGiver095.TailClassOtherShapeBypass: stat = OtherShapePath; break;
                default: stat = UnknownPath; break;
            }
            stat.Over2++;
            if (elapsed >= T5) stat.Over5++;
            if (elapsed >= T10) stat.Over10++;
            if (elapsed >= T20) stat.Over20++;
            if (elapsed >= T50) stat.Over50++;
            if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
        }

        private static string PathSummary(string name, TailStats s)
        {
            double maxUs = s.MaxTicks * 1000000.0 / Stopwatch.Frequency;
            return name + "(>2=" + s.Over2 + ",>5=" + s.Over5 + ",>10=" + s.Over10 +
                   ",>20=" + s.Over20 + ",>50=" + s.Over50 + ",maxUs=" + maxUs.ToString("F1") + ")";
        }

        internal static bool IsRecurringHot(WorkGiver_Scanner scanner)
'@ 'path attribution helpers'

$tail = Replace-OrThrow $tail @'
                   ", unresolved=" + unresolved +
                   ", maxUs=" + globalMaxUs.ToString("F1") +
                   ", top=" + (parts.Count == 0 ? "<none>" : string.Join("; ", parts.ToArray()));
'@ @'
                   ", unresolved=" + unresolved +
                   ", maxUs=" + globalMaxUs.ToString("F1") +
                   ", pathAttribution=[" +
                   PathSummary("preAdmission", PreAdmissionPath) + "; " +
                   PathSummary("supportedUnsliced", SupportedUnslicedPath) + "; " +
                   PathSummary("exactClosure", ExactClosurePath) + "; " +
                   PathSummary("otherShape", OtherShapePath) + "; " +
                   PathSummary("unknown", UnknownPath) + "]" +
                   ", top=" + (parts.Count == 0 ? "<none>" : string.Join("; ", parts.ToArray()));
'@ 'path attribution report'

$tail = Replace-OrThrow $tail @'
        private sealed class TailStats
'@ @'
        private struct TailCallState
        {
            internal long Start;
            internal int PathClass;
        }

        private sealed class TailStats
'@ 'tail call state struct'

$tail = $tail.Replace('[RimMT] V0.9.4 lightweight JobGiver tail buckets installed on ', '[RimMT] V0.9.11 Slow Tail Attribution installed on ')
$tail = $tail.Replace('[RimMT] V0.9.4 JobGiver tail buckets failed closed: ', '[RimMT] V0.9.11 Slow Tail Attribution failed closed: ')
$tail = $tail.Replace('JobGiver tail buckets V0.9.4:', 'JobGiver tail buckets V0.9.11:')
Set-Content $tailPath $tail -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.10-source-reconciled-continuation','0.9.11-slow-tail-attribution')
$boot = $boot.Replace('V0.9.10 Source-Reconciled Continuation initialized','V0.9.11 Slow Tail Attribution initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.10 Source-Reconciled Continuation on-demand report','V0.9.11 Slow Tail Attribution on-demand report')
$diag = $diag.Replace('V0.9.10 Source-Reconciled Continuation; JobGiver tail buckets=ON;', 'V0.9.11 Slow Tail Attribution; JobGiver tail buckets=ON; slow decision-path attribution=ON; structural invalidation split=ON;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.10 Source-Reconciled Continuation','V0.9.11 Slow Tail Attribution')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.10 Source-Reconciled Continuation','RimMT V0.9.11 Slow Tail Attribution')
$about = [regex]::Replace($about, '<description>.*?</description>', '<description>RimMT V0.9.11 Slow Tail Attribution for RimWorld 1.5. Telemetry-only continuation release: V0.9.10 package ownership, logical scanner identity, source reconciliation, slice budgets and live Vanilla authority are unchanged. Existing JobGiver whole-call timing now attributes already-slow calls to pre-admission, supported-but-unsliced, exact-closure bypass, other-shape bypass or unknown paths without adding per-candidate reflection. Continuation structural invalidations are split into package, pawn, map, root and missing-member causes, and handled sliced segments receive their own tail buckets. ReachProfile V0.4.18 and other validated production paths remain unchanged.</description>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.11 Slow Tail Attribution: telemetry-only decision-path tail classification + structural invalidation split; optimization behavior and ReachProfile remain unchanged.'
