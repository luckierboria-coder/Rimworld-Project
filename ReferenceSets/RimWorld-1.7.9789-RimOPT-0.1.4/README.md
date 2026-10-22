# RimWorld 1.7.9789 / RimOPT 0.1.4 reference manifest

Recorded on 2026-10-22 from the user's uploads: `Assembly-CSharp.dll`, `Waylith.dll`, `UnityStubs.dll`, and `RimOPT.zip`. Original file names, byte sizes, SHA-256 checksums and migration status are in [manifest.json](manifest.json).

## Storage status

**Original DLL bytes are NOT stored in this repository.** The repository is public. Do not redistribute the user's original RimWorld/Waylith/UnityStubs game-runtime binaries in it. Only this metadata is committed. This manifest **does not enable later recovery of the actual bytes**. For a real backup without requesting the files again, store them in an authorized private repository, with a binary-capable upload channel.

## Verified assembly identity

The uploaded game `Assembly-CSharp.dll` contains the exact game version **1.7.9789** and `.NETCoreApp,Version=v10.0` metadata. All three assemblies indicate **.NET 10**. The attached RimOPT ZIP contains **0.1.4** source (`Source/RimOPT/`), seven module switches and a **net472** C# project targeting RimWorld 1.6.

## RimOPT 1.7 migration checklist

1. Keep the original 1.6 build and package untouched; add dedicated `1.7/Assemblies/` and appropriate `LoadFolders.xml` mapping.
2. Build a separate `net10.0` project. Replace the old 1.6 `UnityEngine.CoreModule.dll` reference with **1.7** `UnityStubs.dll` + `Waylith.dll` and the 1.7 `Assembly-CSharp.dll`. Use a **1.7/.NET 10** compatible `0Harmony.dll`; no Harmony binary was included in this upload.
3. Audit original targets and signatures, not just their names: `JobGiver_Work.TryIssueJobPackage`, `GenClosest.ClosestThingReachable`, `Reachability.CanReach` and cache clear variants, `WorkGiver_DoBill`, `WorkGiver_Merge`, and roof/growing/snow work-source methods. In particular, any bytecode-dependent work-search short-circuit needs a new 1.7 proof before enabling.
4. Preserve RimOPT's runtime guards, compatibility/foreign-patch checks, original validators, job correctness and per-module disable controls. Prefer safe fallback over potentially incorrect optimization.
5. Validate on actual RimWorld 1.7.9789: Harmony patch installation, no UI or startup errors, work/haul/bill semantics, in-game TPS variance and patch-chain interference with other optimization mods. A successful compile is not sufficient.
6. Avoid uploading user-provided game binaries, decompiled proprietary game sources, saves, personal logs, or configs into this public repository.

**Status:** Metadata archived. 1.7 build, binary storage, API signature verification and game testing have NOT yet been completed. No claim that the original DLLs are recoverable from this repository.

## Technical references

- [RimWorld 1.7 Mod Updates](https://rimworldwiki.com/wiki/Modding_Tutorials/RimWorld_1.7_Mod_Updates)
- [RimRef](https://github.com/krafs/RimRef) (licensed reference assemblies where a matching version is available)
