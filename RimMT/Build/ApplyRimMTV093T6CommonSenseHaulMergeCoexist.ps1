$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T6 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T6 CommonSense HaulMerge Coexist
# T5 proved the sole T4 authority blocker is Common Sense's exact Postfix on Thing.CanStackWith.
# Source review shows that Postfix is monotonic narrowing only: it returns immediately when the
# Vanilla result is false and can only turn an already-true result false based on CompIngredients.
# Therefore T4's negative proof (absence of any same-def/same-stuff/count partner) remains necessary.
# T6 accepts ONLY that exact owner + patch class + method, and ONLY as a Postfix on Thing.CanStackWith.
# Any other foreign Prefix/Postfix/Transpiler/Finalizer still fails open to Vanilla.

$mergePath = 'RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs'
$merge = Get-Content $mergePath -Raw

$merge = Replace-OrThrow $merge @'
        private static long failures;
'@ @'
        private static long failures;
        private static bool commonSenseMonotonicPostfixAccepted;
'@ 'T6 coexistence marker field'

$merge = Replace-OrThrow $merge @'
        private static bool HasForeignPatch(MethodBase method)
        {
            if (method == null) return true;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) ||
                   HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) return true;
            }
            return false;
        }
'@ @'
        private static bool HasForeignPatch(MethodBase method)
        {
            if (method == null) return true;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return false;

            // T6: Common Sense 1.5 patches Thing.CanStackWith with a Postfix that is monotonic
            // narrowing only: it returns immediately when __result is already false, and can only
            // change true -> false based on CompIngredients flags. That cannot invalidate T4's
            // necessary-condition negative proof. No other foreign patch kind or method is accepted.
            if (ReferenceEquals(method, thingCanStack))
            {
                if (HasForeign(info.Prefixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers))
                    return true;
                return HasForeignPostfixExceptAcceptedCommonSense(info.Postfixes);
            }

            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) ||
                   HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool HasForeignPostfixExceptAcceptedCommonSense(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal))
                    continue;
                if (IsAcceptedCommonSenseNarrowingPostfix(patch))
                {
                    commonSenseMonotonicPostfixAccepted = true;
                    continue;
                }
                return true;
            }
            return false;
        }

        private static bool IsAcceptedCommonSenseNarrowingPostfix(Patch patch)
        {
            if (patch == null ||
                !string.Equals(patch.owner, "net.avilmask.rimworld.mod.CommonSense", StringComparison.Ordinal))
                return false;

            MethodInfo method = patch.PatchMethod;
            Type declaring = method == null ? null : method.DeclaringType;
            return method != null &&
                   string.Equals(method.Name, "Postfix", StringComparison.Ordinal) &&
                   declaring != null &&
                   string.Equals(declaring.FullName,
                       "CommonSense.CompIngredients_CanStackWith_CommonSensePatch",
                       StringComparison.Ordinal);
        }
'@ 'T6 exact CommonSense monotonic-postfix coexistence'

$merge = Replace-OrThrow $merge @'
                   ", failures=" + failures +
                   ". Cache lifetime=one synchronous JobGiver_Work package; negative proof=same def/stuff + other partial stack count >= source; survivors run original JobOnThing.";
'@ @'
                   ", failures=" + failures +
                   ", commonSenseMonotonicPostfixAccepted=" + commonSenseMonotonicPostfixAccepted +
                   ". Cache lifetime=one synchronous JobGiver_Work package; negative proof=same def/stuff + other partial stack count >= source; survivors run original JobOnThing.";
'@ 'T6 coexistence summary marker'
Set-Content $mergePath $merge -Encoding UTF8

$censusPath = 'RimMT/Source/RimMT/Diagnostics/HaulMergePatchCensus093T5.cs'
$census = Get-Content $censusPath -Raw
$census = Replace-OrThrow $census @'
                    ". T4 remains unchanged and still fails open on any foreign patch.\n");
'@ @'
                    ". Raw foreign-patch census; T6 may explicitly accept only the exact Common Sense monotonic-narrowing Thing.CanStackWith Postfix.\n");
'@ 'T6 census interpretation marker'
Set-Content $censusPath $census -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t5-haulmerge-patch-census";' 'internal const string Version = "0.9.3-t6-commonsense-haulmerge-coexist";' 'T6 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T5 HaulMerge Patch Census initialized. T4 behavior unchanged; census is measurement-only.' '[RimMT] V0.9.3-T6 CommonSense HaulMerge Coexist initialized. Exact Common Sense monotonic CanStackWith postfix may coexist; all other foreign authority still fails open.' 'T6 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report 'V0.9.3-T5 HaulMerge Patch Census' 'V0.9.3-T6 CommonSense HaulMerge Coexist' 'T6 report title'
$report = Replace-OrThrow $report 'T5 Harmony authority census=measurement-only; S4 early rescue=OFF;' 'T5 Harmony authority census=measurement-only; T6 exact CommonSense monotonic-postfix coexistence; S4 early rescue=OFF;' 'T6 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T5 HaulMerge Patch Census', 'V0.9.3-T6 CommonSense HaulMerge Coexist')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T6: exact Common Sense CompIngredients CanStackWith monotonic-narrowing Postfix is accepted for T4 HaulMerge necessary-condition pruning; every other foreign patch still fails open.'
