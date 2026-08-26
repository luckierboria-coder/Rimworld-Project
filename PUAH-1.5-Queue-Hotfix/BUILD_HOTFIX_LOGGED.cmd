@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "LOG=%~dp0BUILD_LOG.txt"
>"%LOG%" echo PUAH 1.5 Queue Hotfix build log v5.1
>>"%LOG%" echo Started: %date% %time%
>>"%LOG%" echo Folder: %CD%
>>"%LOG%" echo.

echo ============================================================
echo  PUAH 1.5 Queue Hotfix - release build v5.1.1
echo ============================================================
echo.

echo [1/5] Looking for C# compiler...
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found.
  >>"%LOG%" echo [ERROR] csc.exe not found.
  goto :end
)
echo Found: %CSC%
>>"%LOG%" echo CSC=%CSC%

echo.
echo [2/5] Detecting RimWorld...
set "GAME=%~dp0..\.."
for %%I in ("%GAME%") do set "GAME=%%~fI"
if not exist "%GAME%\RimWorldWin64_Data\Managed\Assembly-CSharp.dll" (
  echo [ERROR] RimWorld not found two folders above this mod.
  >>"%LOG%" echo [ERROR] RimWorld not found at %GAME%
  goto :end
)
echo Found RimWorld: %GAME%
>>"%LOG%" echo GAME=%GAME%
set "MANAGED=%GAME%\RimWorldWin64_Data\Managed"
set "OUT=%~dp01.5\Assemblies\PUAHQueueHotfix.dll"

if not exist "%~dp0BuildRefs\0Harmony.dll" (
  echo [ERROR] BuildRefs\0Harmony.dll missing.
  goto :end
)
if not exist "%~dp0Source\Hotfix.cs" (
  echo [ERROR] Source\Hotfix.cs missing.
  goto :end
)
if not exist "%~dp01.5\Assemblies" mkdir "%~dp01.5\Assemblies"
if exist "%OUT%" del /q "%OUT%"

echo.
echo [3/5] Checking references...
if not exist "%MANAGED%\Assembly-CSharp.dll" goto :refsbad
if not exist "%MANAGED%\UnityEngine.CoreModule.dll" goto :refsbad
echo References OK.
goto :compile

:refsbad
echo [ERROR] Required RimWorld managed references are missing.
goto :end

:compile
echo.
echo [4/5] Compiling...
>>"%LOG%" echo.
>>"%LOG%" echo --- Compiler output ---
set "TMPERR=%TEMP%\puah_hotfix_compile_%RANDOM%.txt"
"%CSC%" /nologo /target:library /optimize+ /out:"%OUT%" /reference:"%MANAGED%\Assembly-CSharp.dll" /reference:"%MANAGED%\UnityEngine.CoreModule.dll" /reference:"%~dp0BuildRefs\0Harmony.dll" "%~dp0Source\Hotfix.cs" >"%TMPERR%" 2>&1
set "ERR=%ERRORLEVEL%"
type "%TMPERR%"
type "%TMPERR%" >>"%LOG%"
del /q "%TMPERR%" >nul 2>&1
if not "%ERR%"=="0" (
  echo.
  echo [FAILED] Compiler returned error code %ERR%.
  >>"%LOG%" echo [FAILED] Error code %ERR%
  goto :end
)

if not exist "%OUT%" (
  echo [FAILED] DLL was not created.
  >>"%LOG%" echo [FAILED] DLL missing.
  goto :end
)

echo.
echo [5/5] SUCCESS
echo Built: %OUT%
>>"%LOG%" echo [SUCCESS] %OUT%
echo.
echo Enable "PUAH 1.5 Queue Hotfix V5" AFTER Pick Up And Haul.

:end
echo.
echo ------------------------------------------------------------
echo Build log: %LOG%
echo You can close this window when finished.
echo ------------------------------------------------------------
