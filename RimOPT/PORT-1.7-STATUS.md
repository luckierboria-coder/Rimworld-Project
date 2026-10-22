# RimOPT 0.2.0-alpha — RimWorld 1.7 port candidate

This is a **source port candidate**, not an in-game-verified performance mod. No 1.7 DLL is included until CI compilation succeeds.

## Build and reference policy

- New independent `.NET 10` project: `Source17/RimOPT.csproj`; original `Source/RimOPT/RimOPT.csproj` remains a 1.6/net472 project.
- Uses public Ludeon-authorized `Krafs.Rimworld.Ref` version `1.7.4982-unstable` and `Lib.Harmony` version `2.4.2` at compile time. Runtime `0Harmony.dll` is provided by the Harmony mod and **must not** be bundled with RimOPT.
- User-supplied game files have assembly file version `1.7.9789.37269`; RimRef package version follows a *different* version/build numbering scheme. Exact API behavior for this uploaded game build still needs validation. Never claim matching names prove matching IL semantics.
- DLLs `Assembly-CSharp.dll`, `Waylith.dll`, `UnityStubs.dll` were **not** added to source or GitHub.

## 1.7 design changes

- `LoadFolders.xml`: separate `1.6` and `1.7` assembly selection; leaves original 1.6 DLL intact.
- All 7 optimization toggles default **OFF in 1.7** and use separate persisted keys to prevent 1.6 settings carrying forward silently. You must explicitly enable each module in a test game after compiling.
- The 1.7 bootstrap isolates module install exceptions and avoids scope-dependent installs if its `TryIssueJobPackage` hook cannot be established. No patch chain is silently forced around unsupported targets.
- No algorithmic change has yet been validated. Existing 1.6 Harmony signatures may not match 1.7. Compile errors and runtime tests must guide the next edits.

## Required testing after GitHub build

1. Verify 1.7 DLL loads with Harmony, without crashing on main menu; check `[RimOPT]` logs.
2. Load a disposable save. Verify seven switches all default to OFF, independent of existing 1.6 preferences.
3. Enable one module per restart, check target signatures, Harmony patch-chain behavior, and work correctness (especially bills, merging, reachability and GenClosest).
4. Record benchmark TPS/frame times and compare identical saves, ticks, and work settings against vanilla 1.7.
5. Do **not** label this a stable 1.7 release until the above pass.

## Build locally only if necessary (prefer the connected GitHub Actions workflow)

`dotnet build Source17/RimOPT.csproj -c Release`

To package a compiled 1.7 DLL into the mod, copy it to `1.7/Assemblies/RimOPT.dll`. The source ZIP alone is **not** a functioning RimWorld 1.7 mod.
