@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

echo ================================================
echo  Mrd Bedless Lovin - RimWorld 1.5 Backport Build
echo ================================================
echo.

set "RIMWORLD=%~1"
if defined RIMWORLD goto :checkrim
if defined RIMWORLD_DIR set "RIMWORLD=%RIMWORLD_DIR%"
if defined RIMWORLD goto :checkrim
if exist "F:\Rimworld\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll" set "RIMWORLD=F:\Rimworld\RimWorld"
if defined RIMWORLD goto :checkrim
if exist "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll" set "RIMWORLD=C:\Program Files (x86)\Steam\steamapps\common\RimWorld"
if defined RIMWORLD goto :checkrim
if exist "C:\Program Files\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll" set "RIMWORLD=C:\Program Files\Steam\steamapps\common\RimWorld"

:checkrim
if not defined RIMWORLD goto :norim
set "MANAGED=%RIMWORLD%\RimWorldWin64_Data\Managed"
if not exist "%MANAGED%\Assembly-CSharp.dll" goto :norim

echo RimWorld: %RIMWORLD%

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto :nocsc

echo Compiler: %CSC%
if not exist "1.5\Assemblies" mkdir "1.5\Assemblies"

echo.
echo Compiling...
"%CSC%" /nologo /target:library /optimize+ /langversion:5 ^
 /out:"1.5\Assemblies\LovinAnywhere.dll" ^
 /reference:"%MANAGED%\Assembly-CSharp.dll" ^
 /reference:"%MANAGED%\UnityEngine.CoreModule.dll" ^
 /reference:"%MANAGED%\UnityEngine.dll" ^
 /reference:"%MANAGED%\UnityEngine.IMGUIModule.dll" ^
 /reference:"%MANAGED%\UnityEngine.TextRenderingModule.dll" ^
 "Source\LovinAnywhere.cs" > BUILD_LOG.txt 2>&1

if errorlevel 1 goto :failed

echo SUCCESS
echo Built: %CD%\1.5\Assemblies\LovinAnywhere.dll
echo.
echo Copy this whole folder into RimWorld\Mods, enable it, then test a save.
echo Build log: %CD%\BUILD_LOG.txt
exit /b 0

:failed
echo FAILED
more BUILD_LOG.txt
echo.
echo Send BUILD_LOG.txt back for the next compatibility pass.
exit /b 1

:norim
echo ERROR: RimWorld was not found.
echo Run this BAT with your RimWorld folder as the first argument, for example:
echo Build_RimWorld_1.5.bat "F:\Rimworld\RimWorld"
exit /b 2

:nocsc
echo ERROR: Microsoft .NET Framework C# compiler csc.exe was not found.
exit /b 3
