Mrd Bedless Lovin - RimWorld 1.5 Backport

Purpose
- Backports the original 1.6 mod to RimWorld 1.5.
- Keeps the original packageId and Def names for save compatibility.
- Keeps the Romantic meeting point, joy giver, partner interaction, GotSomeLovin mood memory and Biotech pregnancy path.

Why the DLL is rebuilt
- The supplied original DLL targets the RimWorld 1.6 API.
- This source is intended to compile directly against your installed RimWorld 1.5 Assembly-CSharp.dll.
- API-sensitive partner-job and pregnancy calls use compatibility/reflection paths where practical.

Build
1. Put this folder anywhere, or directly under RimWorld\Mods.
2. Run Build_RimWorld_1.5.bat.
3. The script automatically checks F:\Rimworld\RimWorld first, matching the installation path used in earlier local builds.
4. If RimWorld is elsewhere, drag the RimWorld folder onto the BAT or run:
   Build_RimWorld_1.5.bat "X:\path\to\RimWorld"
5. Successful output: 1.5\Assemblies\LovinAnywhere.dll

Testing checklist
- Game reaches main menu without red errors.
- Romantic meeting point appears under Misc and can be placed.
- A pawn with an existing lover can receive the private-interaction joy job.
- Both pawns move to the meeting point.
- Job completes and both receive GotSomeLovin.
- With Biotech active, no pregnancy-related red error occurs.
- Save/load once after the interaction and re-check the log.

Important
This package contains source + an on-machine build script because the RimWorld 1.5 game assemblies are not included in the uploaded mod archive and should be referenced from your own installation when compiling.
