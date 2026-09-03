$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Unified Lean V25 transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V25 is S4-only. ReachProfile remains pinned to V0.4.18.
# Vehicle Framework 1.5 WorkGiver_CarryToVehicle.FindThingToPack builds a private static
# HashSet<Thing> named neededThings, then its local ValidThing validator checks:
#   neededThings.Contains(thing) && pawn.CanReserve(thing) && !thing.IsForbidden(pawn.Faction)
# Runtime telemetry shows this validator rejecting >2M candidates. We duplicate ONLY the first
# membership negative before the original validator. Survivors still run the original VF validator,
# then RimMT's normal live reachability and final Vanilla-authoritative path.
# If the exact VF 1.5 closure/field shape is absent, or either the local validator or outer
# FindThingToPack has Harmony authority, the optimization fails open.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long actualValidatorCalls;
        private static long failures;
'@ @'
        private static long actualValidatorCalls;
        private static long vehicleMembershipScopes;
        private static long vehicleMembershipPrefilterCalls;
        private static long vehicleMembershipPrefilterRejected;
        private static long vehicleMembershipStateBypass;
        private static long vehicleMembershipAuthorityBypass;
        private static long failures;
'@ 'V25 vehicle membership counters'

$s4 = Replace-OrThrow $s4 @'
        private static readonly Dictionary<Type, bool> TargetedPrefilterAuthorityCache = new Dictionary<Type, bool>();
'@ @'
        private static readonly Dictionary<Type, bool> TargetedPrefilterAuthorityCache = new Dictionary<Type, bool>();
        private static bool vehicleMembershipProbeInitialized;
        private static Type vehicleMembershipClosureType;
        private static FieldInfo vehicleNeededThingsField;
        private static bool vehicleMembershipProbeSafe;
'@ 'V25 vehicle membership reflection cache'

$s4 = Replace-OrThrow $s4 @'
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ @'
            ICollection<Thing> vehicleNeededThings;
            if (kept > 0 && TryGetVehicleMembershipSet(validator, out vehicleNeededThings))
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    vehicleMembershipPrefilterCalls++;
                    Candidate candidate = candidates[i];
                    if (!vehicleNeededThings.Contains(candidate.Thing))
                    {
                        vehicleMembershipPrefilterRejected++;
                        continue;
                    }
                    candidates[write++] = candidate;
                }
                kept = write;
            }

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ 'V25 vehicle membership compaction before original validator'

$s4 = Replace-OrThrow $s4 @'
        private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ @'
        private static bool TryGetVehicleMembershipSet(Predicate<Thing> validator, out ICollection<Thing> neededThings)
        {
            neededThings = null;
            if (validator == null) return false;

            MethodInfo localMethod;
            Type closureType;
            Type ownerType;
            try
            {
                localMethod = validator.Method;
                closureType = localMethod == null ? null : localMethod.DeclaringType;
                ownerType = closureType == null ? null : closureType.DeclaringType;
            }
            catch
            {
                return false;
            }

            // Exact RimWorld 1.5 Vehicle Framework route seen in runtime telemetry.
            if (ownerType == null || ownerType.FullName != "Vehicles.WorkGiver_CarryToVehicle" || localMethod == null)
                return false;
            string methodName = localMethod.Name ?? string.Empty;
            if (methodName.IndexOf("FindThingToPack", StringComparison.Ordinal) < 0 ||
                methodName.IndexOf("ValidThing", StringComparison.Ordinal) < 0)
                return false;

            vehicleMembershipScopes++;
            try
            {
                if (!vehicleMembershipProbeInitialized || vehicleMembershipClosureType != closureType)
                {
                    bool safe = true;

                    Patches localPatches = Harmony.GetPatchInfo(localMethod);
                    if (HasAnyHarmonyAuthority(localPatches)) safe = false;

                    // Be conservative if another mod changes the outer FindThingToPack contract.
                    MethodInfo[] ownerMethods = ownerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < ownerMethods.Length && safe; i++)
                    {
                        MethodInfo method = ownerMethods[i];
                        if (method == null || method.Name != "FindThingToPack") continue;
                        Patches info = Harmony.GetPatchInfo(method);
                        if (HasAnyHarmonyAuthority(info)) safe = false;
                    }

                    FieldInfo field = ownerType.GetField("neededThings",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null || !typeof(ICollection<Thing>).IsAssignableFrom(field.FieldType))
                        safe = false;

                    vehicleMembershipClosureType = closureType;
                    vehicleNeededThingsField = field;
                    vehicleMembershipProbeSafe = safe;
                    vehicleMembershipProbeInitialized = true;
                }

                if (!vehicleMembershipProbeSafe || vehicleNeededThingsField == null)
                {
                    vehicleMembershipAuthorityBypass++;
                    return false;
                }

                neededThings = vehicleNeededThingsField.GetValue(null) as ICollection<Thing>;
                if (neededThings == null)
                {
                    vehicleMembershipStateBypass++;
                    return false;
                }
                return true;
            }
            catch
            {
                vehicleMembershipStateBypass++;
                neededThings = null;
                return false;
            }
        }

        private static bool HasAnyHarmonyAuthority(Patches info)
        {
            return info != null &&
                (info.Prefixes.Count != 0 || info.Postfixes.Count != 0 ||
                 info.Transpilers.Count != 0 || info.Finalizers.Count != 0);
        }

        private static void RecordRoute(RescueRoute route, int validatorCalls, int validatorRejects, int reachRejects, Predicate<Thing> validator)
'@ 'V25 vehicle membership resolver and authority guard'

$s4 = Replace-OrThrow $s4 @'
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected) +
                   ", reachRejected=" + reachRejected +
'@ @'
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected + vehicleMembershipPrefilterRejected) +
                   ", reachRejected=" + reachRejected +
'@ 'V25 include vehicle membership in aggregate prefilter rejects'

$s4 = Replace-OrThrow $s4 @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", failures=" + failures +
'@ @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", vf15MembershipScopes=" + vehicleMembershipScopes +
                   ", vf15MembershipCalls=" + vehicleMembershipPrefilterCalls +
                   ", vf15MembershipRejected=" + vehicleMembershipPrefilterRejected +
                   ", vf15MembershipStateBypass=" + vehicleMembershipStateBypass +
                   ", vf15MembershipAuthorityBypass=" + vehicleMembershipAuthorityBypass +
                   ", failures=" + failures +
'@ 'V25 vehicle membership telemetry summary'

Set-Content $s4Path $s4 -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'S4 tail=32ms + true-validator attribution + authority-safe pruners + VF1.5 membership negative;' 'V25 diagnostics S4 policy label'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied Unified Lean V25 S4 transform: Vehicle Framework 1.5 neededThings membership negative before original validator; ReachProfile remains V0.4.18.'
