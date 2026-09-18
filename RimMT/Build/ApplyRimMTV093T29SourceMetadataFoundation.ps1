$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T29 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T29 Source Metadata Foundation
# - package-local metadata only; no proactive ListerThings/source scan
# - T22 publishes Thing + PositionHeld while doing the full scan it already needed
# - later same-package builds for the same IList may reuse captured metadata after bounded fingerprint validation
# - no validator / Reachability / reservation / Job / priority result is cached

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t28-unified-job-search-transaction";' 'internal const string Version = "0.9.3-t29-source-metadata-foundation";' 'bootstrap version'
$boot=$boot.Replace('[RimMT] V0.9.3-T28 Unified Job Search Transaction initialized.',
                    '[RimMT] V0.9.3-T29 Source Metadata Foundation initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$t22Path='RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
$t22=Get-Content $t22Path -Raw

$pattern='(?s)        internal sealed class CandidateIndex\s*\{.*?\n        internal struct Candidate'
if(-not [regex]::IsMatch($t22,$pattern)){ throw 'T29 anchor missing: T22 CandidateIndex class' }

$replacement=@'
        internal sealed class CandidateIndex
        {
            internal readonly Candidate[] Candidates;
            private readonly SourceMetadata093T29.ThingListMetadata sourceMetadata;
            private readonly Sample[] samples;
            private readonly int count;

            private CandidateIndex(
                Candidate[] candidates,
                SourceMetadata093T29.ThingListMetadata sourceMetadata,
                Sample[] samples,
                int count)
            {
                Candidates = candidates;
                this.sourceMetadata = sourceMetadata;
                this.samples = samples;
                this.count = count;
            }

            internal static CandidateIndex Build(IList list, IntVec3 center)
            {
                if (list == null) return null;

                // T29 never scans a source on a metadata miss. This lookup is package-local and
                // validates only a bounded fingerprint. If present, reuse the Thing+position
                // capture made by an earlier T22 build and derive this center's distance order.
                SourceMetadata093T29.ThingListMetadata metadata;
                if (SourceMetadata093T29.TryGetValid(list, out metadata) && metadata != null)
                {
                    int metadataCount = metadata.Count;
                    SourceMetadata093T29.SourceItem[] items = metadata.Items;
                    if (items == null || items.Length != metadataCount) return null;

                    Candidate[] reused = new Candidate[metadataCount];
                    for (int i = 0; i < metadataCount; i++)
                    {
                        SourceMetadata093T29.SourceItem item = items[i];
                        Thing thing = item.Thing;
                        if (thing == null || !thing.Spawned)
                            return null;

                        int distance = (center - item.Position).LengthHorizontalSquared;
                        reused[i] = new Candidate(thing, distance, i);
                    }

                    SortCandidates(reused);
                    return new CandidateIndex(reused, metadata, null, metadataCount);
                }

                // Miss path is the exact full source scan T22 already had to perform. Capture
                // metadata inside that same pass so T29 adds no second O(n) enumeration.
                int count = list.Count;
                Candidate[] candidates = new Candidate[count];
                SourceMetadata093T29.SourceItem[] captured =
                    new SourceMetadata093T29.SourceItem[count];

                for (int i = 0; i < count; i++)
                {
                    Thing thing = list[i] as Thing;
                    if (thing == null || !thing.Spawned)
                        return null;

                    IntVec3 position = thing.PositionHeld;
                    captured[i] = new SourceMetadata093T29.SourceItem(thing, position);
                    int distance = (center - position).LengthHorizontalSquared;
                    candidates[i] = new Candidate(thing, distance, i);
                }

                SortCandidates(candidates);

                metadata = SourceMetadata093T29.PublishCaptured(list, captured);
                if (metadata != null)
                    return new CandidateIndex(candidates, metadata, null, count);

                // Capacity/scope/fingerprint rejection remains fail-open to T22's previous
                // local fingerprint semantics; T29 is never required for correctness.
                Sample[] samples = BuildLegacySamples(list, count);
                return new CandidateIndex(candidates, null, samples, count);
            }

            private static void SortCandidates(Candidate[] candidates)
            {
                if (candidates == null || candidates.Length <= 1) return;
                Array.Sort(candidates, delegate(Candidate a, Candidate b)
                {
                    int cmp = a.DistanceSquared.CompareTo(b.DistanceSquared);
                    if (cmp != 0) return cmp;
                    return a.SourceIndex.CompareTo(b.SourceIndex);
                });
            }

            private static Sample[] BuildLegacySamples(IList list, int count)
            {
                int sampleCount = Math.Min(8, count);
                Sample[] localSamples = new Sample[sampleCount];
                if (sampleCount == 0) return localSamples;

                for (int s = 0; s < sampleCount; s++)
                {
                    int index = sampleCount == 1 ? 0 :
                        (int)((long)s * (count - 1) / (sampleCount - 1));
                    Thing thing = list[index] as Thing;
                    if (thing == null || !thing.Spawned) return new Sample[0];
                    localSamples[s] = new Sample(index, thing, thing.PositionHeld);
                }
                return localSamples;
            }

            internal bool FingerprintMatches(IList list)
            {
                if (sourceMetadata != null)
                    return SourceMetadata093T29.IsCurrent(list, sourceMetadata);

                if (list == null || list.Count != count || samples == null)
                    return false;

                for (int i = 0; i < samples.Length; i++)
                {
                    Sample sample = samples[i];
                    Thing currentThing = list[sample.Index] as Thing;
                    if (!ReferenceEquals(currentThing, sample.Thing) ||
                        currentThing == null || !currentThing.Spawned ||
                        currentThing.PositionHeld != sample.Position)
                        return false;
                }
                return true;
            }
        }

        internal struct Candidate
'@

$t22=[regex]::Replace($t22,$pattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement },1)
$t22=$t22.Replace(
  '. Scope=one synchronous JobGiver_Work package, repeated IList source+center only, priorityGetter=null, lookInHaulSources=false, all members spawned; sorted by distance then source order; original live validator is called exactly once per visited candidate.";',
  '. Scope=one synchronous JobGiver_Work package, repeated IList source+center only, priorityGetter=null, lookInHaulSources=false, all members spawned; T29 may reuse package-local Thing+PositionHeld metadata across center-specific builds after bounded fingerprint validation; sorted by distance then source order; original live validator is called exactly once per visited candidate.";')
Set-Content $t22Path $t22 -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T29 Source Metadata Foundation')
$reportAnchor='            sb.AppendLine(JobSearchPackageContext093T28.Summary());'
if(-not $report.Contains($reportAnchor)){ throw 'T29 anchor missing: production report T28 summary' }
$report=$report.Replace($reportAnchor,
  $reportAnchor + [Environment]::NewLine + '            sb.AppendLine(SourceMetadata093T29.Summary());')
$report=$report.Replace(
  'T28 package-local false readiness proof;',
  'T28 package-local false readiness proof; T29 package-local source metadata capture/reuse;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T29 Source Metadata Foundation')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T29 for RimWorld 1.5. T28 unified synchronous Job Search transactions are retained. T29 adds a package-local Source Metadata foundation: T22 may publish Thing reference + PositionHeld metadata during a full IList scan it already performs, then reuse that capture for later center-specific builds after bounded live fingerprint validation. T29 never initiates ListerThings/source scans and never caches validator, Reachability, reservation, Job, priority, or cross-package gameplay results. Uncertain or stale metadata fails open.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.6.0";' 'internal const string Version = "0.7.0";' 'Diagnostics v0.7 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
$dr=Replace-OrThrow $dr @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.JobSearchTransaction093T20",
'@ @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.SourceMetadata093T29",
            "RimMT.JobSearchTransaction093T20",
'@ 'Diagnostics T29 reflection bridge'
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.6','RimMT Diagnostics v0.7')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional measurement-only diagnostics companion for RimMT T29. v0.7 surfaces the package-local Source Metadata foundation alongside the T28 unified Job Search context and existing bounded attribution. Disable this companion when measuring pure production performance.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T29 Source Metadata Foundation + Diagnostics v0.7.'
