@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "LOG=%~dp0BUILD_LOG.txt"
>"%LOG%" echo PUAH 1.5 Queue Hotfix build log v5.2
>>"%LOG%" echo Started: %date% %time%

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto :failcsc

set "GAME=%~dp0..\.."
for %%I in ("%GAME%") do set "GAME=%%~fI"
set "MANAGED=%GAME%\RimWorldWin64_Data\Managed"
set "OUT=%~dp01.5\Assemblies\PUAHQueueHotfix.dll"

if not exist "%MANAGED%\Assembly-CSharp.dll" goto :failrefs
if not exist "%~dp0BuildRefs\0Harmony.dll" goto :failrefs
if not exist "%~dp0Source\Hotfix.cs" goto :failrefs
if not exist "%~dp0Source\Performance.cs" goto :failrefs
if not exist "%~dp01.5\Assemblies" mkdir "%~dp01.5\Assemblies"
if exist "%OUT%" del /q "%OUT%"

"%CSC%" /nologo /target:library /optimize+ /out:"%OUT%" ^
 /reference:"%MANAGED%\Assembly-CSharp.dll" ^
 /reference:"%MANAGED%\UnityEngine.CoreModule.dll" ^
 /reference:"%~dp0BuildRefs\0Harmony.dll" ^
 "%~dp0Source\Hotfix.cs" "%~dp0Source\Performance.cs" >>"%LOG%" 2>&1
if errorlevel 1 goto :failbuild

echo SUCCESS: %OUT%
>>"%LOG%" echo SUCCESS: %OUT%
goto :end

:failcsc
echo ERROR: csc.exe not found.
>>"%LOG%" echo ERROR: csc.exe not found.
goto :end

:failrefs
echo ERROR: required references/source missing.
>>"%LOG%" echo ERROR: required references/source missing.
goto :end

:failbuild
echo ERROR: compile failed. See BUILD_LOG.txt.
>>"%LOG%" echo ERROR: compile failed.

:end
pause
