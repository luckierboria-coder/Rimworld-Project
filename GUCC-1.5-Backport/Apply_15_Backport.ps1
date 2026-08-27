param(
    [Parameter(Mandatory=$true)] [string]$ModRoot
)

$ErrorActionPreference = 'Stop'
Write-Host "[GUCC 1.5] Applying backport to $ModRoot"

# Remove 1.6 payload and create 1.5 output folder.
Remove-Item "$ModRoot\1.6" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$ModRoot\1.5\Assemblies" | Out-Null
Remove-Item "$ModRoot\Source\References" -Recurse -Force -ErrorAction SilentlyContinue

# About.xml: target the user's Owlchemist Giddy-Up 2 build on RimWorld 1.5.
$aboutPath = "$ModRoot\About\About.xml"
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('<li>1.6</li>', '<li>1.5</li>')
$about = $about.Replace('<modVersion>0.8.5</modVersion>', '<modVersion>0.8.5-1.5-backport</modVersion>')
$about = $about.Replace('MemeGoddess.GiddyUp', 'Owlchemist.GiddyUp')
$about = $about.Replace('Giddy-Up 2 - Continued', 'Giddy-Up 2')
Set-Content $aboutPath $about -Encoding UTF8

# Project: compile against the exact RimWorld 1.5.4063 public reference API.
$projPath = "$ModRoot\Source\GiddyUpCavalryCharge.csproj"
$proj = Get-Content $projPath -Raw
$proj = $proj.Replace('<TargetFramework>net48</TargetFramework>', '<TargetFramework>net472</TargetFramework>')
$proj = $proj.Replace('<OutputPath>..\1.6\Assemblies\</OutputPath>', '<OutputPath>..\1.5\Assemblies\</OutputPath>')
$proj = $proj.Replace('<PackageReference Include="Krafs.Rimworld.Ref" Version="1.6.4633" />', '<PackageReference Include="Krafs.Rimworld.Ref" Version="1.5.4063" />')
$proj = [regex]::Replace($proj, '(?s)\s*<ItemGroup>\s*<Reference Include="GiddyUpCore">.*?</ItemGroup>\s*', "`r`n")
Set-Content $projPath $proj -Encoding UTF8

# Owlchemist Giddy-Up 2 (1.5) uses ExtendedDataStorage.GUComp/_store,
# not the Continued 1.6 Singleton/GetExtendedPawnData API.  Use reflection so
# this DLL remains ABI-tolerant while matching the user's actual GiddyUpCore.dll.
$giddyPath = "$ModRoot\Source\Core\GiddyUpAccess.cs"
@'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace GiddyUpCavalryCharge
{
internal static class GiddyUpAccess
{
    private const string StorageTypeName = "GiddyUp.ExtendedDataStorage";
    private const string MountabilityTypeName = "GiddyUp.IsMountableUtility";
    private const string WaitForRiderDriverTypeName = "GiddyUpRideAndRoll.Jobs.JobDriver_WaitForRider";

    private static bool storageResolved;
    private static FieldInfo guCompField;
    private static FieldInfo isMountedField;
    private static FieldInfo storeField;
    private static FieldInfo mountField;
    private static FieldInfo reservedMountField;

    private static bool mountabilityResolved;
    private static MethodInfo isEverMountableMethod;

    private static object GetExtendedPawnData(Pawn pawn)
    {
        if (pawn == null) return null;
        ResolveStorage();
        if (guCompField == null || storeField == null) return null;
        try
        {
            var comp = guCompField.GetValue(null);
            if (comp == null) return null;
            var store = storeField.GetValue(comp) as IDictionary;
            if (store == null || !store.Contains(pawn.thingIDNumber)) return null;
            return store[pawn.thingIDNumber];
        }
        catch { return null; }
    }

    public static bool TryGetMount(Pawn rider, out Pawn mount)
    {
        mount = null;
        if (rider == null) return false;
        ResolveStorage();
        if (isMountedField == null) return false;
        try
        {
            var mountedSet = isMountedField.GetValue(null) as ICollection<int>;
            if (mountedSet == null || !mountedSet.Contains(rider.thingIDNumber)) return false;
            var data = GetExtendedPawnData(rider);
            if (data == null || mountField == null) return false;
            mount = mountField.GetValue(data) as Pawn;
            return mount != null && mount.Spawned && !mount.Dead && !mount.Downed;
        }
        catch { mount = null; return false; }
    }

    public static bool TryGetAssignedMount(Pawn rider, out Pawn mount)
    {
        if (TryGetMount(rider, out mount)) return true;
        mount = null;
        var data = GetExtendedPawnData(rider);
        if (data == null || reservedMountField == null) return false;
        try
        {
            mount = reservedMountField.GetValue(data) as Pawn;
            return mount != null && !mount.Destroyed && !mount.Dead;
        }
        catch { mount = null; return false; }
    }

    public static bool IsPotentialMount(Pawn pawn)
    {
        if (pawn == null || pawn.RaceProps == null) return false;
        ResolveMountabilityMethod();
        if (isEverMountableMethod == null) return pawn.RaceProps.Animal;
        try
        {
            var parameters = isEverMountableMethod.GetParameters();
            if (parameters.Length != 1) return pawn.RaceProps.Animal;
            object argument = null;
            var parameter = parameters[0].ParameterType;
            if (parameter.IsInstanceOfType(pawn)) argument = pawn;
            else if (pawn.def != null && parameter.IsInstanceOfType(pawn.def)) argument = pawn.def;
            else if (pawn.kindDef != null && parameter.IsInstanceOfType(pawn.kindDef)) argument = pawn.kindDef;
            if (argument == null) return pawn.RaceProps.Animal;
            return (bool)isEverMountableMethod.Invoke(null, new[] { argument });
        }
        catch { return pawn.RaceProps.Animal; }
    }

    public static bool IsWaitForRiderJob(Pawn rider, Pawn mount)
    {
        if (rider == null || mount == null || mount.jobs == null || mount.jobs.curDriver == null || mount.CurJob == null)
            return false;
        return mount.jobs.curDriver.GetType().FullName == WaitForRiderDriverTypeName && mount.CurJob.targetA.Thing == rider;
    }

    public static bool ReplaceWaitForRiderWithMountedJob(Pawn rider, Pawn mount)
    {
        if (!IsWaitForRiderJob(rider, mount)) return false;
        Pawn currentMount;
        if (!TryGetMount(rider, out currentMount) || currentMount != mount) return false;
        var mountedDef = DefDatabase<JobDef>.GetNamedSilentFail("Mounted");
        if (mountedDef == null || mount.jobs == null || mount.jobs.jobQueue == null) return false;
        var mountedJob = JobMaker.MakeJob(mountedDef, rider);
        mountedJob.count = 1;
        mount.jobs.jobQueue.EnqueueFirst(mountedJob);
        mount.jobs.EndCurrentJob(JobCondition.InterruptForced);
        return true;
    }

    public static Job MakeMountJob(Pawn rider, Pawn mount)
    {
        if (rider == null || mount == null || mount.Dead || mount.Destroyed) return null;
        var mountDef = DefDatabase<JobDef>.GetNamedSilentFail("Mount");
        if (mountDef == null) return null;
        var job = JobMaker.MakeJob(mountDef, mount);
        job.count = 1;
        job.playerForced = true;
        return job;
    }

    private static void ResolveStorage()
    {
        if (storageResolved) return;
        storageResolved = true;
        var storageType = AccessTools.TypeByName(StorageTypeName);
        if (storageType == null) return;
        guCompField = AccessTools.Field(storageType, "GUComp");
        isMountedField = AccessTools.Field(storageType, "isMounted");
        storeField = AccessTools.Field(storageType, "_store");
        var dataType = AccessTools.TypeByName("GiddyUp.ExtendedPawnData");
        if (dataType != null)
        {
            mountField = AccessTools.Field(dataType, "mount");
            reservedMountField = AccessTools.Field(dataType, "reservedMount");
        }
    }

    private static void ResolveMountabilityMethod()
    {
        if (mountabilityResolved) return;
        mountabilityResolved = true;
        var type = AccessTools.TypeByName(MountabilityTypeName);
        if (type == null) return;
        var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method.Name != "IsEverMountable" || method.ReturnType != typeof(bool)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1)
            {
                isEverMountableMethod = method;
                return;
            }
        }
    }
}
}
'@ | Set-Content $giddyPath -Encoding UTF8

@'
Giddy-Up: Cavalry Charge 0.8.5 - RimWorld 1.5.4063 Backport

Target: RimWorld 1.5.4063 + Harmony + Owlchemist.GiddyUp.
The 1.6 Giddy-Up Continued compile-time API dependency is replaced with an ABI-tolerant runtime adapter for Owlchemist Giddy-Up 2.
'@ | Set-Content "$ModRoot\README_1.5_BACKPORT.txt" -Encoding UTF8

Write-Host "[GUCC 1.5] Backport patch applied."
