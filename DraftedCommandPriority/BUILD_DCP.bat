@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title Drafted Command Priority V0.1 - Local Builder
cd /d "%~dp0"

set "ROOT=%CD%"
set "SOURCE=%ROOT%\Source\DraftedCommandPriority\DraftedCommandPriority.cs"
set "PROJECT=%ROOT%\Source\DraftedCommandPriority\DraftedCommandPriority.csproj"
set "ASSEMBLIES=%ROOT%\1.5\Assemblies"
set "DLL=%ASSEMBLIES%\DraftedCommandPriority.dll"
set "OUT=%ROOT%\BuildOutput"
set "STAGE=%OUT%\DraftedCommandPriority"
set "ZIP=%OUT%\DraftedCommandPriority_V0.1_Playtest.zip"
set "LOG=%ROOT%\BUILD_LOG.txt"
set "RW=F:\Rimworld\RimWorld"
set "BUILDMETHOD="

cls
> "%LOG%" echo Drafted Command Priority V0.1 local build log
>>"%LOG%" echo Started: %DATE% %TIME%
>>"%LOG%" echo.

echo ============================================================
echo  Drafted Command Priority V0.1 - Local Builder
echo ============================================================
echo.

if not exist "%SOURCE%" (
  echo [ERROR] Source file not found:
  echo %SOURCE%
  goto :buildfail
)

if not exist "%RW%\RimWorldWin64.exe" (
  if exist "C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64.exe" set "RW=C:\Program Files (x86)\Steam\steamapps\common\RimWorld"
)

if not exist "%RW%\RimWorldWin64.exe" (
  echo [ERROR] RimWorld was not found automatically.
  echo Expected first choice: F:\Rimworld\RimWorld
  echo.
  set /p "RW=Paste your RimWorld folder path, then press Enter: "
)

if not exist "%RW%\RimWorldWin64.exe" (
  echo [ERROR] Invalid RimWorld path: %RW%
  goto :buildfail
)

set "MANAGED=%RW%\RimWorldWin64_Data\Managed"
if not exist "%MANAGED%\Assembly-CSharp.dll" (
  echo [ERROR] Missing Assembly-CSharp.dll under:
  echo %MANAGED%
  goto :buildfail
)
if not exist "%MANAGED%\UnityEngine.CoreModule.dll" (
  echo [ERROR] Missing UnityEngine.CoreModule.dll under:
  echo %MANAGED%
  goto :buildfail
)

if not exist "%ASSEMBLIES%" mkdir "%ASSEMBLIES%" >nul 2>nul
if exist "%DLL%" del /f /q "%DLL%" >nul 2>nul

rem ------------------------------------------------------------
rem Method A: local .NET Framework csc + live RimWorld/Harmony refs
rem ------------------------------------------------------------
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

set "HARMONY="

rem 1) Prefer actual Harmony mod runtime assemblies.
for %%H in (
  "%RW%\Mods\Harmony\1.5\Assemblies\0Harmony.dll"
  "%RW%\Mods\Harmony\Current\Assemblies\0Harmony.dll"
  "%RW%\Mods\brrainz.harmony\1.5\Assemblies\0Harmony.dll"
  "%RW%\Mods\brrainz.harmony\Current\Assemblies\0Harmony.dll"
  "%RW%\Mods\2009463077\1.5\Assemblies\0Harmony.dll"
  "%RW%\Mods\2009463077\Current\Assemblies\0Harmony.dll"
) do (
  if not defined HARMONY if exist "%%~fH" set "HARMONY=%%~fH"
)

rem 2) Search Mods recursively, but reject Source/packages copies and prefer Assemblies paths.
if not defined HARMONY if exist "%RW%\Mods" (
  for /f "delims=" %%H in ('dir /b /s /a-d "%RW%\Mods\0Harmony.dll" 2^>nul') do (
    if not defined HARMONY (
      set "CAND=%%~fH"
      set "FILTERED=!CAND:\Source\=!"
      if /i "!FILTERED!"=="!CAND!" (
        set "FILTERED=!CAND:\packages\=!"
        if /i "!FILTERED!"=="!CAND!" (
          set "FILTERED=!CAND:\Assemblies\=!"
          if /i not "!FILTERED!"=="!CAND!" if exist "!CAND!" set "HARMONY=!CAND!"
        )
      )
    )
  )
)

rem 3) Search common Steam Workshop locations for Harmony workshop id 2009463077.
if not defined HARMONY (
  for %%D in (
    "C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077"
    "C:\Program Files\Steam\steamapps\workshop\content\294100\2009463077"
    "D:\SteamLibrary\steamapps\workshop\content\294100\2009463077"
    "E:\SteamLibrary\steamapps\workshop\content\294100\2009463077"
    "F:\SteamLibrary\steamapps\workshop\content\294100\2009463077"
    "F:\Steam\steamapps\workshop\content\294100\2009463077"
    "F:\Rimworld\steamapps\workshop\content\294100\2009463077"
    "G:\SteamLibrary\steamapps\workshop\content\294100\2009463077"
  ) do (
    if not defined HARMONY if exist "%%~D" (
      for /f "delims=" %%H in ('dir /b /s /a-d "%%~D\0Harmony.dll" 2^>nul') do (
        if not defined HARMONY if exist "%%~fH" set "HARMONY=%%~fH"
      )
    )
  )
)

rem 4) If CSC is available but Harmony still cannot be found, let the user paste it.
if exist "%CSC%" if not defined HARMONY (
  echo [INFO] Harmony runtime 0Harmony.dll was not found automatically.
  echo Typical path: ...\2009463077\Current\Assemblies\0Harmony.dll
  echo.
  set /p "HARMONY=Paste the full path to runtime 0Harmony.dll, or press Enter for dotnet fallback: "
  if defined HARMONY (
    set HARMONY=!HARMONY:"=!
    if not exist "!HARMONY!" (
      echo [WARNING] The supplied Harmony path does not exist:
      echo !HARMONY!
      set "HARMONY="
    )
  )
)

if exist "%CSC%" if defined HARMONY if exist "%HARMONY%" goto :build_csc
goto :build_dotnet

:build_csc
echo [1/5] Local compiler found:
echo %CSC%
echo [2/5] Harmony reference found:
echo %HARMONY%
echo [3/5] Compiling directly against RimWorld 1.5...
>>"%LOG%" echo Build method: CSC
>>"%LOG%" echo CSC=%CSC%
>>"%LOG%" echo RimWorld=%RW%
>>"%LOG%" echo Harmony=%HARMONY%
>>"%LOG%" echo.

"%CSC%" /nologo /target:library /optimize+ /out:"%DLL%" /reference:"%MANAGED%\Assembly-CSharp.dll" /reference:"%MANAGED%\UnityEngine.CoreModule.dll" /reference:"%HARMONY%" "%SOURCE%" >>"%LOG%" 2>&1
if not errorlevel 1 if exist "%DLL%" (
  set "BUILDMETHOD=Windows csc + local RimWorld references"
  goto :package
)

echo.
echo Direct CSC compile did not succeed. Trying dotnet SDK fallback...
echo See BUILD_LOG.txt for the CSC diagnostics.
echo.
if exist "%DLL%" del /f /q "%DLL%" >nul 2>nul
goto :build_dotnet

:build_dotnet
set "DOTNET=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
  ) else if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
  ) else (
    echo [ERROR] Neither a usable local CSC+Harmony setup nor a .NET SDK was found.
    echo.
    echo Install/enable Harmony, or paste the real runtime 0Harmony.dll path when prompted.
    goto :buildfail
  )
)

"%DOTNET%" --list-sdks > "%TEMP%\dcp_sdks.txt" 2>nul
for %%A in ("%TEMP%\dcp_sdks.txt") do if %%~zA==0 (
  del "%TEMP%\dcp_sdks.txt" >nul 2>nul
  echo [ERROR] dotnet is present, but no .NET SDK is installed.
  goto :buildfail
)
del "%TEMP%\dcp_sdks.txt" >nul 2>nul

if not exist "%PROJECT%" (
  echo [ERROR] Project file missing for dotnet fallback:
  echo %PROJECT%
  goto :buildfail
)

echo [1/5] Using dotnet SDK fallback...
echo [2/5] Restoring NuGet references...
>>"%LOG%" echo Build method: dotnet SDK fallback
"%DOTNET%" restore "%PROJECT%" >>"%LOG%" 2>&1
if errorlevel 1 goto :buildfail

echo [3/5] Building Release DLL...
"%DOTNET%" build "%PROJECT%" --configuration Release --no-restore >>"%LOG%" 2>&1
if errorlevel 1 goto :buildfail
if not exist "%DLL%" goto :buildfail
set "BUILDMETHOD=dotnet SDK + NuGet reference assemblies"

goto :package

:package
echo [4/5] Packaging playtest mod...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if not exist "%OUT%" mkdir "%OUT%" >nul 2>nul
mkdir "%STAGE%" >nul 2>nul
xcopy "%ROOT%\About" "%STAGE%\About\" /e /i /q /y >nul
xcopy "%ROOT%\Languages" "%STAGE%\Languages\" /e /i /q /y >nul
xcopy "%ROOT%\1.5" "%STAGE%\1.5\" /e /i /q /y >nul
copy /y "%ROOT%\README.md" "%STAGE%\README.md" >nul

if not exist "%STAGE%\1.5\Assemblies\DraftedCommandPriority.dll" (
  echo [ERROR] Staging failed: compiled DLL is missing.
  goto :buildfail
)

if exist "%ZIP%" del /f /q "%ZIP%" >nul 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGE%' -DestinationPath '%ZIP%' -Force" >>"%LOG%" 2>&1
if errorlevel 1 goto :buildfail
if not exist "%ZIP%" goto :buildfail

echo [5/5] Verifying output...
>>"%LOG%" echo.
>>"%LOG%" echo Build succeeded via: %BUILDMETHOD%
for %%F in ("%DLL%") do >>"%LOG%" echo DLL: %%~fF ^(%%~zF bytes^)
for %%F in ("%ZIP%") do >>"%LOG%" echo ZIP: %%~fF ^(%%~zF bytes^)

echo.
echo ============================================================
echo  BUILD SUCCESS
 echo ============================================================
echo Build method: %BUILDMETHOD%
for %%F in ("%DLL%") do echo DLL: %%~fF  ^(%%~zF bytes^)
for %%F in ("%ZIP%") do echo ZIP: %%~fF  ^(%%~zF bytes^)

where certutil >nul 2>nul
if not errorlevel 1 (
  echo.
  echo SHA256:
  certutil -hashfile "%ZIP%" SHA256 | findstr /v /c:"CertUtil"
)

echo.
echo Build log:
echo %LOG%
echo.

if exist "%RW%\Mods" (
  choice /c YN /n /m "Copy this build to RimWorld\Mods\DraftedCommandPriority now? [Y/N] "
  if errorlevel 2 goto :done
  if errorlevel 1 goto :install
)

goto :done

:install
set "TARGET=%RW%\Mods\DraftedCommandPriority"
if not exist "%TARGET%" mkdir "%TARGET%" >nul 2>nul
xcopy "%STAGE%\*" "%TARGET%\" /e /i /q /y >nul
if errorlevel 1 (
  echo.
  echo [WARNING] Build succeeded, but automatic copy to RimWorld failed.
  echo Extract/copy the ZIP from BuildOutput manually.
  goto :done
)
echo.
echo Installed to:
echo %TARGET%
echo Enable "Drafted Command Priority" after Harmony in the mod list.
goto :done

:buildfail
echo.
echo ============================================================
echo  BUILD FAILED
 echo ============================================================
echo.
echo Build log:
echo %LOG%
echo.
if exist "%LOG%" type "%LOG%"
echo.
echo Send BUILD_LOG.txt to me and I can fix the exact reference/compiler issue.
pause
exit /b 10

:done
echo.
echo You can close this window.
pause
exit /b 0
