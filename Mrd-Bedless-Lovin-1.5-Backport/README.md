# Mrd Bedless Lovin — RimWorld 1.5 Backport

RimWorld 1.5 backport of **Mrd Bedless Lovin**.

## Status
- Compiles successfully against the user's RimWorld 1.5 installation.
- Keeps the original package ID and Def names for save compatibility.
- English + Simplified Chinese localization included.
- Includes the private interaction JoyGiver/JobDriver, GotSomeLovin memory, and Biotech pregnancy compatibility path.

## Build
Run `Build_RimWorld_1.5.bat` on Windows. It references the RimWorld 1.5 assemblies from the local game installation and outputs:

`1.5/Assemblies/LovinAnywhere.dll`

The tested build uses C# 5 (`/langversion:5`) for compatibility with the .NET Framework `csc.exe` bundled with Windows.

## Runtime test checklist
1. Game reaches the main menu without LovinAnywhere red errors.
2. `Romantic meeting point / 浪漫幽会点` appears under Misc.
3. A pawn with an existing lover can receive the private interaction joy job.
4. Both pawns reach the meeting point and complete the interaction.
5. Both receive `GotSomeLovin`.
6. With Biotech active, pregnancy logic produces no red errors.
7. Save/load once after an interaction and re-check the log.

## Binary/assets note
The repository connector used for this commit writes UTF-8 source files only, so the compiled DLL and PNG assets are not included in this source commit. Build the DLL locally with the included BAT and copy the original PNG assets from the packaged backport when testing.
